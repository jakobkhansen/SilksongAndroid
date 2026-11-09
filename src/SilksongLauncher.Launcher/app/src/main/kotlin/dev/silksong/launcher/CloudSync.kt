// CloudSync — high-level orchestrator for cloud-save sync operations.
//
// Pull: analyzePull → pullItems. Compares each cloud entry's timestamp
// against the matching local file's mtime, buckets them into safe
// (cloud strictly newer or local-doesn't-exist) and conflicting (local
// strictly newer) sets. Caller is expected to feed pullItems either
// just the safe set (no-prompt path) or the union of safe + conflicts
// (after the user OK'd overwriting local progress with older cloud
// data). All-or-nothing — never some slots from cloud, others left
// local.
//
// Push: analyzePush → pushItems. Symmetric: safe (local newer or
// brand-new file) vs conflicting (cloud newer). Caller decides
// whether to include conflicts based on user confirmation.
//
// Target directory:
//   ${context.getExternalFilesDir(null)}/<userId-or-default>/<filename>
//
// Silksong's DesktopPlatform.Awake() sets:
//   saveDirPath = Application.persistentDataPath +
//                 "/" + (onlineSubsystem?.UserId ?: "default")
//
// On Android, Application.persistentDataPath resolves to
// /storage/emulated/0/Android/data/dev.silksong.player/files/ — the
// SAME path as context.getExternalFilesDir(null) for our launcher
// (we share the package with the game).
//
// We use "default" for the userId subdirectory because the Android
// build has no Steam-tied OnlineSubsystem yet; the game will fall
// back to "default" on first launch. Once the launcher learns the
// signed-in SteamID we can write to <steamid>/ too.
//
// Per-file rename via Steam's directory-prefix convention: Silksong
// cloud filenames carry a Windows-style remote-store prefix
//   %WinAppDataLocalLow%Team Cherry/Hollow Knight Silksong/<accountId>/userN.dat
// while local Android filenames are just `userN.dat`. We map by
// basename in both directions and reuse the original cloud prefix
// when uploading to an existing entry; for new files we fall back to
// the standard Silksong layout with the signed-in user's account id.
//
// Conflict model: per-direction last-write-wins by timestamp with a
// single "X is newer for N file(s) — overwrite?" prompt before any
// clobbering. Both directions are all-or-nothing once the analysis
// runs — the user either authorises the clobber and we apply the
// FULL set (safe + conflicts) or the operation aborts entirely.

package dev.silksong.launcher

import android.content.Context
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.channelFlow
import kotlinx.coroutines.flow.flowOn
import kotlinx.coroutines.joinAll
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Semaphore
import kotlinx.coroutines.sync.withPermit
import java.io.File
import java.util.concurrent.ConcurrentLinkedQueue
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicLong

object CloudSync {

    // Hollow Knight: Silksong. Same as the .NET launcher.
    const val APP_ID = 1030300

    // Cloud transfers are latency-bound: every file is its own
    // begin/commit RPC pair plus a CDN round-trip, and Steam has no
    // multi-file transfer API. Running several at once is the only way
    // to cut the wall-clock cost. Bounded to stay well under Steam's
    // per-file TooManyPending lease and any global pending cap — 4
    // captures most of the win at low risk. Tune here if needed.
    private const val MAX_PARALLEL_TRANSFERS = 4

    /**
     * Silksong reads from <persistentDataPath>/default/userN.dat when
     * no online subsystem provides a userId. We sync cloud saves to
     * that exact directory so the game picks them up on its next
     * launch.
     *
     * Defined by [SaveDir], which is also what prepares the directory
     * before a launch -- one definition, because a cloud pull writing
     * to a different directory than the one the game reads is a bug
     * with no symptom until somebody loses a save.
     */
    private const val SAVE_SUBDIR = SaveDir.SUBDIR

    /**
     * Root of Silksong's cloud paths. `%WinAppDataLocalLow%` is Steam's
     * per-platform path token; on Windows the desktop client expands it
     * to LocalLow/Team Cherry/…, on Linux to a platform-equivalent path.
     *
     * Saves live one folder deeper, in `<root><accountId>/`: the desktop
     * game's save dir is persistentDataPath + "/" + UserId, and
     * SteamOnlineSubsystem.UserId is GetSteamID().GetAccountID(). See
     * [canonicalPrefix].
     */
    private const val CLOUD_ROOT =
        "%WinAppDataLocalLow%Team Cherry/Hollow Knight Silksong/"

    /** Phases emitted by [pullItems] and [pushItems]. */
    sealed class Event {
        data class EnumeratingFiles(val message: String) : Event()
        data class FilesListed(val files: List<SteamCloudClient.CloudFile>) : Event()
        data class FileStarted(val filename: String, val index: Int, val total: Int) : Event()
        data class FileDone(val filename: String, val bytesWritten: Long) : Event()
        data class FileFailed(val filename: String, val error: String) : Event()
        data class Complete(val ok: Int, val failed: Int, val totalBytes: Long) : Event()
    }

    /**
     * A single local-target save to download from the cloud, plus the
     * matching cloud metadata so the caller can show progress info or
     * decide which subset to apply.
     */
    data class PullItem(
        val cloudFile: SteamCloudClient.CloudFile,
        val localFile: File,
        val cloudTimestampUnix: Long,
        val localMtimeUnix: Long,
    )

    /**
     * Result of [analyzePull]. Symmetric to [PushAnalysis]:
     *   - [toDownload]: cloud files that are strictly newer than the
     *     local copy (or that have no local counterpart yet). Safe to
     *     fetch without prompting.
     *   - [conflicts]: cloud files where LOCAL is newer than the cloud
     *     copy. A safe auto-pull skips these (so we don't clobber a
     *     fresh local save with an older cloud version); manual / UI
     *     callers can present them as overrides if they want.
     *   - [skipped]: cloud files whose timestamp matches local
     *     exactly. No-op — would only burn bandwidth.
     */
    data class PullAnalysis(
        val toDownload: List<PullItem>,
        val conflicts: List<PullItem>,
        val skipped: List<String>,
    ) {
        val hasConflicts: Boolean get() = conflicts.isNotEmpty()
    }

    /**
     * Mirror of [analyzePush] for the pull side. Logs in, enumerates,
     * compares each cloud file's timestamp against the matching local
     * file's mtime, and buckets them by direction:
     *
     *   cloud > local (or no local file) → toDownload
     *   local > cloud                    → conflicts
     *   timestamps equal                 → skipped
     *
     * Used by auto-pull, which only ever downloads [PullAnalysis.toDownload]
     * — the conflicts list is for diagnostics only there. Manual pull
     * does NOT use this analyser (it still clobbers local with the
     * full cloud set via [pullAll]); that matches what the user
     * explicitly asked for when they originally wired the manual Pull
     * button.
     */
    suspend fun analyzePull(
        context: Context,
        credentials: TokenStore.Credentials,
    ): PullAnalysis = kotlinx.coroutines.withContext(Dispatchers.IO) {
        val saveDir = saveDirFor(context)
        saveDir.mkdirs()

        SteamSession().use { session ->
            session.logOn(credentials)
            val cloud = SteamCloudClient(session)
            LauncherLog.log("Pull analyze: enumerating cloud files…")
            val allCloud = cloud.enumerateFiles(APP_ID)
            val prefix = derivePrefix(allCloud, canonicalPrefix(session))
            val cloudFiles = filterRootLevel(allCloud, prefix)
            if (cloudFiles.size != allCloud.size) {
                LauncherLog.log(
                    "Pull analyze: ignoring ${allCloud.size - cloudFiles.size} non-root cloud entr(ies) " +
                        "(Restore_Points etc. — desktop-only backups)"
                )
            }

            val toDownload = mutableListOf<PullItem>()
            val conflicts = mutableListOf<PullItem>()
            val skipped = mutableListOf<String>()

            for (cf in cloudFiles) {
                val basename = sanitizeFilename(cf.filename)
                val local = File(saveDir, basename)
                val localMtime = if (local.isFile) local.lastModified() / 1000L else -1L
                val item = PullItem(
                    cloudFile = cf,
                    localFile = local,
                    cloudTimestampUnix = cf.timestampUnix,
                    localMtimeUnix = localMtime,
                )
                when {
                    localMtime < 0 -> {
                        toDownload.add(item)
                        LauncherLog.log(
                            "  to-download (new): $basename cloud=${cf.timestampUnix} size=${cf.size}"
                        )
                    }
                    cf.timestampUnix > localMtime -> {
                        toDownload.add(item)
                        LauncherLog.log(
                            "  to-download (cloud-newer): $basename local=$localMtime cloud=${cf.timestampUnix} delta=${cf.timestampUnix - localMtime}s sizes=${local.length()}↔${cf.size}"
                        )
                    }
                    cf.timestampUnix == localMtime -> skipped.add(basename)
                    else -> {
                        conflicts.add(item)
                        LauncherLog.log(
                            "  local-newer: $basename local=$localMtime cloud=${cf.timestampUnix} delta=${localMtime - cf.timestampUnix}s"
                        )
                    }
                }
            }

            LauncherLog.log(
                "Pull analyze: ${toDownload.size} to download, ${conflicts.size} local-newer (skipped), " +
                    "${skipped.size} unchanged"
            )
            PullAnalysis(toDownload, conflicts, skipped)
        }
    }

    /**
     * Downloads [items] in order, writing each into the local save
     * directory. Caller is expected to feed this the [PullAnalysis.toDownload]
     * list (potentially augmented with [PullAnalysis.conflicts] for a
     * "force overwrite" path, though no current caller does that).
     * Opens its own session — same rationale as [pushItems].
     */
    fun pullItems(
        context: Context,
        credentials: TokenStore.Credentials,
        items: List<PullItem>,
    ): Flow<Event> = channelFlow {
        if (items.isEmpty()) {
            send(Event.Complete(0, 0, 0))
            return@channelFlow
        }
        val saveDir = saveDirFor(context)
        saveDir.mkdirs()

        SteamSession().use { session ->
            session.logOn(credentials)
            val cloud = SteamCloudClient(session)

            val gate = Semaphore(MAX_PARALLEL_TRANSFERS)
            val ok = AtomicInteger(0)
            val failed = AtomicInteger(0)
            val totalBytes = AtomicLong(0L)
            val started = AtomicInteger(0)

            // Fan out downloads, at most MAX_PARALLEL_TRANSFERS in flight.
            // Each file is independent and its failure is caught per-item,
            // so one bad download never cancels its siblings. channelFlow
            // makes send() safe to call from these child coroutines.
            items.map { item ->
                launch(Dispatchers.IO) {
                    gate.withPermit {
                        send(Event.FileStarted(item.cloudFile.filename, started.incrementAndGet(), items.size))
                        try {
                            val bytes = cloud.downloadFile(APP_ID, item.cloudFile.filename)
                            item.localFile.parentFile?.mkdirs()
                            item.localFile.writeBytes(bytes)
                            // Match the cloud timestamp on disk so a subsequent
                            // analyzePull recognises this file as in-sync rather
                            // than re-downloading on the next start.
                            item.localFile.setLastModified(item.cloudTimestampUnix * 1000L)
                            totalBytes.addAndGet(bytes.size.toLong())
                            ok.incrementAndGet()
                            LauncherLog.log("  ↓ ${item.localFile.name}: ${bytes.size} bytes")
                            send(Event.FileDone(item.localFile.name, bytes.size.toLong()))
                        } catch (t: Throwable) {
                            failed.incrementAndGet()
                            val msg = t.message ?: t.javaClass.simpleName
                            LauncherLog.log("  ↓ ${item.localFile.name}: FAILED — $msg")
                            android.util.Log.e("SilksongLauncher.Cloud", "pull failed for ${item.cloudFile.filename}", t)
                            send(Event.FileFailed(item.localFile.name, msg))
                        }
                    }
                }
            }.joinAll()

            LauncherLog.log("Cloud pull (safe) done: ${ok.get()} ok, ${failed.get()} failed, ${totalBytes.get()} bytes")
            send(Event.Complete(ok.get(), failed.get(), totalBytes.get()))
        }
    }.flowOn(Dispatchers.IO)

    /**
     * A single local save that's a candidate for upload, paired with
     * the cloud destination path we'll write to. [cloudTimestampUnix]
     * is -1 if the cloud has no counterpart yet (i.e. a new file).
     */
    data class PushItem(
        val localFile: File,
        val cloudPath: String,
        val localMtimeUnix: Long,
        val cloudTimestampUnix: Long,
    )

    /**
     * Result of [analyzePush]:
     *   - [toUpload]: files where local mtime ≥ cloud (or cloud doesn't
     *     have it). These are safe to push without asking.
     *   - [conflicts]: files where cloud is NEWER than local. Pushing
     *     these would clobber a remote write the user hasn't seen. UI
     *     should prompt before including them.
     *   - [skipped]: local files where cloud already has the exact same
     *     timestamp (down to the second). Not uploaded — push would
     *     just churn the server-side timestamp for no gain.
     */
    data class PushAnalysis(
        val toUpload: List<PushItem>,
        val conflicts: List<PushItem>,
        val skipped: List<String>,
        /**
         * Cloud entries with no matching local file. Silksong
         * rotates its `userN.dat.bakM` backups: it keeps creating
         * new ones with higher M (e.g. bak12, bak13, …) and prunes
         * the old ones once it hits its in-game backup cap. Those
         * pruned files stay on Steam Cloud forever unless we delete
         * them, which then makes every subsequent pull
         * re-download them as "new" — Silksong locally deletes
         * them again on the next save, and we loop.
         *
         * pushItems deletes these on the cloud side AFTER any
         * uploads succeed. Two guards keep it safe:
         *   1. analyzePush returns an EMPTY list here when no local
         *      files were visible at all (external storage
         *      unavailable, save dir missing…), so a transient read
         *      failure can never wipe the whole cloud save set.
         *   2. pushFlow runs a push even when this is the only
         *      non-empty bucket (nothing to upload), so a delete-only
         *      push still prunes orphans instead of skipping them.
         */
        val toDelete: List<SteamCloudClient.CloudFile>,
    ) {
        val hasConflicts: Boolean get() = conflicts.isNotEmpty()
        val all: List<PushItem> get() = toUpload + conflicts
    }

    /**
     * Logs in, enumerates the cloud, walks the local save directory and
     * partitions every `user*.dat` (and its sidecars) into upload /
     * conflict / skip buckets. Returns synchronously; no Flow, since
     * the UI normally wants the full picture before deciding whether
     * to show a confirmation dialog. Throws on log-on failure or
     * Steam-side errors.
     */
    suspend fun analyzePush(
        context: Context,
        credentials: TokenStore.Credentials,
    ): PushAnalysis = kotlinx.coroutines.withContext(Dispatchers.IO) {
        val saveDir = saveDirFor(context)
        val locals = collectLocalSaveFiles(saveDir)
        LauncherLog.log("Push analyze: found ${locals.size} local save file(s) in ${saveDir.absolutePath}")

        SteamSession().use { session ->
            session.logOn(credentials)
            val cloud = SteamCloudClient(session)
            LauncherLog.log("Push analyze: enumerating cloud files…")
            val allCloud = cloud.enumerateFiles(APP_ID)
            val cloudPrefix = derivePrefix(allCloud, canonicalPrefix(session))
            val cloudFiles = filterRootLevel(allCloud, cloudPrefix)
            if (cloudFiles.size != allCloud.size) {
                LauncherLog.log(
                    "Push analyze: ignoring ${allCloud.size - cloudFiles.size} non-root cloud entr(ies) " +
                        "(Restore_Points etc. — desktop-only backups)"
                )
            }

            // Map cloud entries by basename. After the root-level
            // filter above, basenames are unique 1:1 — desktop-only
            // subfolder duplicates have been removed.
            val cloudByBasename = cloudFiles.associateBy { sanitizeFilename(it.filename) }

            val toUpload = mutableListOf<PushItem>()
            val conflicts = mutableListOf<PushItem>()
            val skipped = mutableListOf<String>()

            val localBasenames = locals.map { it.name }.toSet()
            // Cloud entries the local side no longer has — typically
            // Silksong's rotated-out userN.dat.bakM backups. See
            // PushAnalysis.toDelete kdoc.
            //
            // CRITICAL safety: only ever prune when we actually saw
            // local files. An empty `locals` (external storage
            // unmounted, app data wiped before a sync, the save dir
            // not yet created…) would otherwise leave `localBasenames`
            // empty, flag EVERY cloud entry as an orphan, and wipe the
            // user's entire cloud save set. No visible local files →
            // delete nothing.
            val toDelete = if (locals.isEmpty()) {
                emptyList()
            } else {
                cloudFiles.filter { sanitizeFilename(it.filename) !in localBasenames }
            }

            for (local in locals) {
                val basename = local.name
                val mtimeSec = local.lastModified() / 1000L
                val cloud = cloudByBasename[basename]
                if (cloud == null) {
                    // New file — push it.
                    toUpload.add(
                        PushItem(
                            localFile = local,
                            cloudPath = cloudPrefix + basename,
                            localMtimeUnix = mtimeSec,
                            cloudTimestampUnix = -1,
                        )
                    )
                    continue
                }
                val item = PushItem(
                    localFile = local,
                    cloudPath = cloud.filename,
                    localMtimeUnix = mtimeSec,
                    cloudTimestampUnix = cloud.timestampUnix,
                )
                when {
                    mtimeSec == cloud.timestampUnix -> skipped.add(basename)
                    mtimeSec > cloud.timestampUnix -> toUpload.add(item)
                    else -> conflicts.add(item)
                }
            }

            LauncherLog.log(
                "Push analyze: ${toUpload.size} to upload, ${conflicts.size} conflict(s), " +
                    "${skipped.size} unchanged, ${toDelete.size} cloud orphan(s) to delete"
            )
            PushAnalysis(toUpload, conflicts, skipped, toDelete)
        }
    }

    /**
     * Performs the actual upload of [items] (which the caller built
     * from [PushAnalysis], optionally including conflicts the user
     * confirmed). Opens its own session — analyze + push are
     * independent operations; the small extra login cost is worth not
     * having to thread a SteamSession across UI boundaries.
     */
    fun pushItems(
        credentials: TokenStore.Credentials,
        items: List<PushItem>,
        toDelete: List<SteamCloudClient.CloudFile> = emptyList(),
    ): Flow<Event> = channelFlow {
        if (items.isEmpty() && toDelete.isEmpty()) {
            send(Event.Complete(0, 0, 0))
            return@channelFlow
        }

        SteamSession().use { session ->
            session.logOn(credentials)
            val cloud = SteamCloudClient(session)

            val gate = Semaphore(MAX_PARALLEL_TRANSFERS)
            val ok = AtomicInteger(0)
            val failed = AtomicInteger(0)
            val totalBytes = AtomicLong(0L)
            val started = AtomicInteger(0)
            val succeeded = ConcurrentLinkedQueue<PushItem>()

            // Fan out uploads, at most MAX_PARALLEL_TRANSFERS in flight.
            // Per-file errors are caught so one failure never cancels the
            // rest; TooManyPending is a per-file lease, so distinct files
            // uploading concurrently are safe. channelFlow makes send()
            // safe to call from these child coroutines.
            items.map { item ->
                launch(Dispatchers.IO) {
                    gate.withPermit {
                        send(Event.FileStarted(item.localFile.name, started.incrementAndGet(), items.size))
                        try {
                            val content = item.localFile.readBytes()
                            cloud.uploadFile(
                                appId = APP_ID,
                                cloudPath = item.cloudPath,
                                rawContent = content,
                                timestampUnix = item.localMtimeUnix,
                            )
                            totalBytes.addAndGet(content.size.toLong())
                            ok.incrementAndGet()
                            succeeded.add(item)
                            LauncherLog.log(
                                "  ↑ ${item.localFile.name}: ${content.size} bytes -> ${item.cloudPath} (ts=${item.localMtimeUnix})"
                            )
                            send(Event.FileDone(item.localFile.name, content.size.toLong()))
                        } catch (t: Throwable) {
                            failed.incrementAndGet()
                            val msg = t.message ?: t.javaClass.simpleName
                            LauncherLog.log("  ↑ ${item.localFile.name}: FAILED — $msg")
                            android.util.Log.e("SilksongLauncher.Cloud", "push failed for ${item.cloudPath}", t)
                            send(Event.FileFailed(item.localFile.name, msg))
                        }
                    }
                }
            }.joinAll()

            // Drop cloud-side orphans (files local no longer has).
            // Done AFTER all uploads finish so a partial-failure scenario
            // doesn't lose data — we only ever remove from cloud what we
            // KNOW exists somewhere as the desktop copy already (the user
            // pulled it once and the game then expired it). Failures here
            // are non-fatal; they'll be retried next push. Parallelised
            // with the same bound.
            toDelete.map { orphan ->
                launch(Dispatchers.IO) {
                    gate.withPermit {
                        try {
                            cloud.deleteFile(APP_ID, orphan.filename)
                            LauncherLog.log("  ✗ deleted cloud orphan: ${sanitizeFilename(orphan.filename)}")
                        } catch (t: Throwable) {
                            val msg = t.message ?: t.javaClass.simpleName
                            LauncherLog.log("  ✗ delete failed for ${orphan.filename}: $msg")
                            android.util.Log.e("SilksongLauncher.Cloud", "delete failed for ${orphan.filename}", t)
                        }
                    }
                }
            }.joinAll()

            // Re-align local mtimes with whatever Steam ended up
            // storing for each file. Steam dedupes uploads by SHA-1
            // of the raw bytes: if the file we just uploaded has
            // the same content as the existing cloud copy, the
            // upload succeeds but the cloud's timestamp is NOT
            // updated — and we'd then perpetually re-detect
            // "local-newer" for that file (the game touches mtime
            // even when the bytes don't change — common for
            // `shared.dat`, which gets rewritten with identical 305
            // bytes on every launch). One re-enumeration after the
            // push lets us snap local mtime to cloud's authoritative
            // timestamp for each succeeded file, breaking the loop.
            //
            // It also proves the upload landed: Steam has accepted an
            // upload and then not stored it (a path outside Silksong's
            // cloud folder), and counting those as "ok" hid that from
            // the user on every push.
            if (succeeded.isNotEmpty()) {
                try {
                    val after = cloud.enumerateFiles(APP_ID).associateBy { it.filename }
                    for (item in succeeded) {
                        val cur = after[item.cloudPath]
                        if (cur == null) {
                            ok.decrementAndGet()
                            failed.incrementAndGet()
                            LauncherLog.log("  ↑ ${item.localFile.name}: FAILED — not on Steam Cloud after upload (${item.cloudPath})")
                            continue
                        }
                        item.localFile.setLastModified(cur.timestampUnix * 1000L)
                    }
                } catch (t: Throwable) {
                    LauncherLog.log("  (post-push verification skipped: ${t.message ?: t.javaClass.simpleName})")
                }
            }

            LauncherLog.log("Cloud push done: ${ok.get()} ok, ${failed.get()} failed, ${totalBytes.get()} bytes, ${toDelete.size} deleted")
            send(Event.Complete(ok.get(), failed.get(), totalBytes.get()))
        }
    }.flowOn(Dispatchers.IO)

    private fun saveDirFor(context: Context): File = SaveDir.of(context)

    /**
     * Local save files we consider candidates for push: every plain
     * file directly inside the save directory (skipping
     * subdirectories and dot-prefixed hidden files, but otherwise
     * not filtering by extension). Silksong scatters its writes
     * across `user1.dat`, `user1.dat.bak1`, `user1_1.0.30000.dat`,
     * `shared.dat`, `shared.dat.bak`, `restoreData2.dat`,
     * `NodeLrestoreData1.dat`, …, and there's no guarantee the
     * extension set won't grow in a patch. Sticking to an
     * extension allow-list lost us pushes of `.bak*` files
     * previously; better to round-trip whatever Silksong's writers
     * leave in the directory.
     *
     * We DO skip dot-files because Android filesystems occasionally
     * deposit `.thumbs` / `.nomedia` / similar artefacts in
     * persistent dirs, and there's no reason to round-trip those
     * through Steam Cloud.
     */
    private fun collectLocalSaveFiles(saveDir: File): List<File> {
        if (!saveDir.isDirectory) return emptyList()
        val children = saveDir.listFiles() ?: return emptyList()
        return children
            .filter { it.isFile && !it.name.startsWith(".") }
            .sortedBy { it.name }
    }

    /**
     * Derives the cloud-path directory prefix (everything up to and
     * including the last '/') of the user's root save folder.
     *
     * [canonical] (`<root><accountId>/`, the folder the desktop game
     * itself writes) wins whenever the cloud is empty or already holds
     * something under it. An empty cloud used to fall back to the bare
     * root with no account folder, and Steam silently dropped every
     * upload there — a first push from Android never reached the cloud.
     *
     * Otherwise we sniff the cloud entry with the FEWEST path segments:
     * Silksong on desktop scatters historical backups across
     * `Restore_Points1/`, `Restore_Points2/` etc., and we want the
     * prefix those folders live UNDER, not one of the subfolders.
     */
    private fun derivePrefix(
        cloudFiles: List<SteamCloudClient.CloudFile>,
        canonical: String,
    ): String {
        if (cloudFiles.isEmpty() || cloudFiles.any { it.filename.startsWith(canonical) })
            return canonical
        val shortest = cloudFiles.minBy { f -> f.filename.count { it == '/' } }
        val lastSlash = shortest.filename.lastIndexOf('/')
        return if (lastSlash >= 0) shortest.filename.substring(0, lastSlash + 1) else canonical
    }

    /** `<root><accountId>/` for the signed-in user of [session]. */
    private fun canonicalPrefix(session: SteamSession): String {
        val accountId = session.steamClient.steamID?.accountID ?: 0L
        if (accountId == 0L)
            throw RuntimeException("Steam did not report an account id for this sign-in")
        return "$CLOUD_ROOT$accountId/"
    }

    /**
     * Keeps only the cloud entries whose path is exactly
     * `<prefix><basename>` — i.e., directly under the root save
     * folder, no intermediate subdirectories. Drops things like
     *
     *   `%WinAppDataLocalLow%/…/85122579/Restore_Points1/user2.dat.bak4`
     *
     * which are Silksong desktop's "Steam Cloud Conflict UI" historical
     * snapshots; the Android game neither reads nor writes them, and
     * keeping them in our analysis caused basename collisions with
     * the root entries that broke push/pull symmetry (the same local
     * file would sometimes upload to the subfolder path and leave the
     * actual root entry stale).
     */
    private fun filterRootLevel(
        cloudFiles: List<SteamCloudClient.CloudFile>,
        prefix: String,
    ): List<SteamCloudClient.CloudFile> =
        cloudFiles.filter { cf ->
            val rest = cf.filename.removePrefix(prefix)
            // No remaining slash → file sits directly under the prefix.
            // (If the file doesn't even share the prefix it'll also pass
            // this check — but then it's an unrelated entry from some
            // other Silksong client layout, and matching it by basename
            // is the best we can do anyway. Empirically Silksong only
            // emits one prefix scheme.)
            !rest.contains('/')
        }

    // Steam cloud filenames for Silksong come back with Windows-style
    // "remote save root" path prefixes — e.g.
    //   %WinAppDataLocalLow%Team Cherry/Hollow Knight Silksong/85122579/user1.dat
    // The leading %TOKEN% segment is a Steam-defined path variable that
    // expands per-platform on the desktop client; the directory tail is
    // the Windows save layout (per-Steam-ID subfolder). None of that
    // applies on Android — Silksong reads bare userN.dat (and its
    // .bak / _<version> sidecars) from <persistentDataPath>/default/.
    //
    // Strip everything up to and including the last '/' so we end up
    // with just the basename. Also strip any "user://" prefix (Godot/
    // Unity convention Steam sometimes prepends) and normalise
    // backslashes that show up on legacy Windows-format entries.
    private fun sanitizeFilename(raw: String): String {
        val normalised = raw.removePrefix("user://").replace('\\', '/')
        val lastSlash = normalised.lastIndexOf('/')
        return if (lastSlash >= 0) normalised.substring(lastSlash + 1) else normalised
    }
}
