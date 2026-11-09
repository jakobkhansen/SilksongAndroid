// SteamCloudClient — wraps JavaSteam's `Cloud` unified service for the
// two operations Phase 1b/1c need:
//
//   enumerateFiles(appId)            → list all CloudFile metadata
//   downloadFile(appId, filename)    → byte[] (HTTP fetch + ZIP decompress
//                                      if Steam served compressed)
//
// Mirrors the .NET launcher's SteamKit2CloudSaveStore architecture but
// flat — no manifest cache or write queue (Phase 1c/1d adds the upload
// side). The download protocol is straight from the C# implementation:
//
//   1. Cloud.ClientFileDownload returns { url_host, url_path, use_https,
//      request_headers, file_size, raw_file_size }.
//   2. HTTP GET url_host + url_path with the headers attached.
//   3. If raw_file_size > 0 AND raw_file_size != file_size AND the
//      payload begins with the PK\x03\x04 ZIP magic, decompress as a
//      single-entry ZIP archive.
//
// HTTP is via OkHttp 5 (already a transitive dep through ktor/JavaSteam).

package dev.silksong.launcher

import `in`.dragonbra.javasteam.enums.EResult
import `in`.dragonbra.javasteam.protobufs.steamclient.SteammessagesCloudSteamclient.CCloud_ClientBeginFileUpload_Request
import `in`.dragonbra.javasteam.protobufs.steamclient.SteammessagesCloudSteamclient.CCloud_ClientCommitFileUpload_Request
import `in`.dragonbra.javasteam.protobufs.steamclient.SteammessagesCloudSteamclient.CCloud_ClientDeleteFile_Request
import `in`.dragonbra.javasteam.protobufs.steamclient.SteammessagesCloudSteamclient.CCloud_ClientFileDownload_Request
import `in`.dragonbra.javasteam.protobufs.steamclient.SteammessagesCloudSteamclient.CCloud_EnumerateUserFiles_Request
import `in`.dragonbra.javasteam.rpc.service.Cloud
import `in`.dragonbra.javasteam.steam.handlers.steamunifiedmessages.SteamUnifiedMessages
import com.google.protobuf.ByteString
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.security.MessageDigest
import java.util.concurrent.TimeUnit
import java.util.zip.ZipInputStream

class SteamCloudClient(session: SteamSession) {

    private val cloud: Cloud = session.steamClient
        .getHandler(SteamUnifiedMessages::class.java)!!
        .createService(Cloud::class.java)

    private val http: OkHttpClient = OkHttpClient.Builder()
        .connectTimeout(30, TimeUnit.SECONDS)
        .readTimeout(60, TimeUnit.SECONDS)
        .callTimeout(120, TimeUnit.SECONDS)
        .build()

    data class CloudFile(
        val filename: String,
        val size: Long,
        val timestampUnix: Long,
    )

    /** Enumerates every cloud file the user has for [appId]. Paginated. */
    fun enumerateFiles(appId: Int): List<CloudFile> {
        val all = mutableListOf<CloudFile>()
        var startIndex = 0
        val pageSize = 500
        while (true) {
            val req = CCloud_EnumerateUserFiles_Request.newBuilder()
                .setAppid(appId)
                .setStartIndex(startIndex)
                .setCount(pageSize)
                .build()
            val resp = cloud.enumerateUserFiles(req).toFuture().get(30, TimeUnit.SECONDS)
                ?: throw RuntimeException("enumerateUserFiles returned no response")
            val body = resp.body
                ?: throw RuntimeException("enumerateUserFiles response missing body")
            val files = body.filesList
            if (files.isEmpty()) break
            for (f in files) {
                all.add(
                    CloudFile(
                        filename = f.filename,
                        size = f.fileSize.toLong(),
                        timestampUnix = f.timestamp,
                    )
                )
            }
            startIndex += files.size
            if (files.size < pageSize) break
        }
        return all
    }

    /**
     * Downloads a single cloud file. Returns the raw bytes after any
     * ZIP decompression. Throws on Steam-side errors, HTTP errors, or
     * timeouts.
     */
    fun downloadFile(appId: Int, filename: String): ByteArray {
        val req = CCloud_ClientFileDownload_Request.newBuilder()
            .setAppid(appId)
            .setFilename(filename)
            .build()
        val resp = cloud.clientFileDownload(req).toFuture().get(30, TimeUnit.SECONDS)
            ?: throw RuntimeException("clientFileDownload returned no response")
        val body = resp.body
            ?: throw RuntimeException("clientFileDownload response missing body")

        if (body.urlHost.isNullOrEmpty())
            throw RuntimeException("Cloud download for $filename: no URL")

        val scheme = if (body.useHttps) "https" else "http"
        val url = scheme + "://" + body.urlHost + body.urlPath

        val httpReqBuilder = Request.Builder().url(url)
        for (h in body.requestHeadersList) {
            httpReqBuilder.addHeader(h.name, h.value)
        }
        val httpResp = http.newCall(httpReqBuilder.build()).execute()
        try {
            if (!httpResp.isSuccessful)
                throw RuntimeException("HTTP ${httpResp.code} fetching $filename")
            val raw = httpResp.body?.bytes() ?: ByteArray(0)

            // Steam may serve the file compressed. The .NET launcher
            // detects this via the ZIP magic header (PK\x03\x04) instead
            // of trusting body.encrypted etc., which has been seen to
            // misreport. We do the same.
            val rawFileSize = body.rawFileSize
            val fileSize = body.fileSize
            val isZipped = rawFileSize > 0 &&
                rawFileSize != fileSize &&
                raw.size >= 4 &&
                raw[0] == 0x50.toByte() &&
                raw[1] == 0x4B.toByte() &&
                raw[2] == 0x03.toByte() &&
                raw[3] == 0x04.toByte()

            return if (isZipped) decompressZip(raw) else raw
        } finally {
            httpResp.close()
        }
    }

    private fun decompressZip(zipBytes: ByteArray): ByteArray {
        // Steam wraps the file in a single-entry ZIP archive named "data".
        ZipInputStream(ByteArrayInputStream(zipBytes)).use { zip ->
            val entry = zip.nextEntry
                ?: throw RuntimeException("ZIP archive has no entries")
            val out = ByteArrayOutputStream(entry.size.toInt().coerceAtLeast(4096))
            val buf = ByteArray(8192)
            while (true) {
                val n = zip.read(buf)
                if (n <= 0) break
                out.write(buf, 0, n)
            }
            return out.toByteArray()
        }
    }

    /**
     * Uploads [rawContent] to the cloud as [cloudPath] for [appId],
     * stamped with [timestampUnix] (server uses this for conflict
     * resolution + WhichOneIsNewer comparisons across clients).
     *
     * Mirrors SteamKit2CloudSaveStore.UploadFileAsync in the .NET
     * launcher:
     *
     *   1. SHA1 the RAW (pre-compression) bytes — this is the identity
     *      Steam uses to dedupe and the commit step echoes it back to
     *      confirm we shipped what we said we'd ship.
     *   2. ZIP-compress if it actually saves bytes.
     *   3. Cloud.ClientBeginFileUpload → server returns one or more
     *      ClientCloudFileUploadBlockDetails entries describing the
     *      HTTP transfers we need to perform.
     *   4. For each block, HTTP PUT (or POST when http_method == 2)
     *      to url_host + url_path with the request_headers attached
     *      and either the slice (block_offset .. +block_length) of
     *      the upload bytes or explicit_body_data if the server
     *      supplied it.
     *   5. Cloud.ClientCommitFileUpload with transfer_succeeded =
     *      true/false. The commit MUST run even on failure so Steam
     *      releases its server-side reservation; otherwise subsequent
     *      uploads of the same file return TooManyPending until the
     *      lease expires.
     */
    fun uploadFile(
        appId: Int,
        cloudPath: String,
        rawContent: ByteArray,
        timestampUnix: Long,
    ) {
        val sha1 = MessageDigest.getInstance("SHA-1").digest(rawContent)
        val shaBytes = ByteString.copyFrom(sha1)
        val (uploadBytes, _) = CloudCompression.compress(rawContent)

        val beginReq = CCloud_ClientBeginFileUpload_Request.newBuilder()
            .setAppid(appId)
            .setFilename(cloudPath)
            .setFileSize(uploadBytes.size)
            .setRawFileSize(rawContent.size)
            .setFileSha(shaBytes)
            .setTimeStamp(timestampUnix)
            .setCanEncrypt(false)
            .setIsSharedFile(false)
            .build()

        val beginResp = cloud.clientBeginFileUpload(beginReq)
            .toFuture().get(30, TimeUnit.SECONDS)
            ?: throw RuntimeException("clientBeginFileUpload returned no response")
        if (beginResp.result != EResult.OK)
            throw RuntimeException("clientBeginFileUpload for $cloudPath returned ${beginResp.result}")
        val begin = beginResp.body
            ?: throw RuntimeException("clientBeginFileUpload response missing body")

        // The commit step MUST run no matter how the block transfers
        // go — Steam holds a server-side lease on the filename until
        // we commit (success OR failure) and rejects re-uploads of
        // the same file with TooManyPending until the lease times out
        // (~minutes). Track success and report it in the commit body.
        var allBlocksOk = false
        var committed = false
        val octetStream = "application/octet-stream".toMediaType()
        try {
            for (block in begin.blockRequestsList) {
                val scheme = if (block.useHttps) "https" else "http"
                val url = scheme + "://" + block.urlHost + block.urlPath

                val body = if (block.explicitBodyData != null && block.explicitBodyData.size() > 0) {
                    block.explicitBodyData.toByteArray()
                } else {
                    val from = block.blockOffset.toInt()
                    val to = from + block.blockLength
                    uploadBytes.sliceArray(from until to)
                }

                val reqBuilder = Request.Builder().url(url)
                for (h in block.requestHeadersList) {
                    reqBuilder.addHeader(h.name, h.value)
                }

                // http_method enum: 1 = PUT (default), 2 = POST.
                // Matches the .NET launcher's mapping.
                val rb = body.toRequestBody(octetStream)
                if (block.httpMethod == 2) reqBuilder.post(rb) else reqBuilder.put(rb)

                val resp = http.newCall(reqBuilder.build()).execute()
                resp.use {
                    if (!it.isSuccessful)
                        throw RuntimeException("HTTP ${it.code} uploading block of $cloudPath")
                }
            }
            allBlocksOk = true
        } finally {
            val commitReq = CCloud_ClientCommitFileUpload_Request.newBuilder()
                .setAppid(appId)
                .setFilename(cloudPath)
                .setFileSha(shaBytes)
                .setTransferSucceeded(allBlocksOk)
                .build()
            try {
                val commitResp = cloud.clientCommitFileUpload(commitReq).toFuture().get(30, TimeUnit.SECONDS)
                committed = commitResp != null && commitResp.result == EResult.OK &&
                    // No blocks means Steam already holds these bytes; it
                    // has nothing new to commit then.
                    (commitResp.body?.fileCommitted == true || begin.blockRequestsCount == 0)
                if (allBlocksOk && !committed)
                    LauncherLog.log("Cloud commit rejected for $cloudPath: ${commitResp?.result}")
            } catch (t: Throwable) {
                LauncherLog.log("Cloud commit failed for $cloudPath: ${t.message}")
            }
        }

        if (!allBlocksOk || !committed)
            throw RuntimeException("Cloud upload failed for $cloudPath")
    }

    /**
     * Deletes [cloudPath] from the cloud. `isExplicitDelete` true
     * tells Steam this was a user-initiated delete (vs an implicit
     * cleanup) — desktop Steam Cloud uses the flag to skip the
     * recycle-bin / undo history. We pass true because every call
     * site here corresponds to "the local file is gone, the cloud
     * copy is orphaned, drop it".
     */
    fun deleteFile(appId: Int, cloudPath: String) {
        val req = CCloud_ClientDeleteFile_Request.newBuilder()
            .setAppid(appId)
            .setFilename(cloudPath)
            .setIsExplicitDelete(true)
            .build()
        val resp = cloud.clientDeleteFile(req).toFuture().get(30, TimeUnit.SECONDS)
            ?: throw RuntimeException("clientDeleteFile returned no response")
        if (resp.result != `in`.dragonbra.javasteam.enums.EResult.OK)
            throw RuntimeException("clientDeleteFile returned ${resp.result}")
    }
}
