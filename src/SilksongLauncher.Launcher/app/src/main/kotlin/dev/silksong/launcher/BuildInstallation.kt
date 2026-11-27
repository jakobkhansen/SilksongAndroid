package dev.silksong.launcher

import android.content.res.AssetManager
import java.io.File
import java.io.FileOutputStream
import java.io.IOException
import java.nio.file.Files
import java.nio.file.StandardCopyOption
import java.security.MessageDigest

/** Readiness belongs to the whole installation, not to an individual cached build step. */
object BuildInstallation {
    private fun marker(pkg: File) = File(pkg, ".built")

    fun signature(assets: AssetManager): String {
        val md = MessageDigest.getInstance("SHA-256")
        // The engine is not among the assets -- it is downloaded on the device
        // -- but a game built against one engine cannot run on another, so an
        // update that changes it must count as a different build.
        md.update(UnityFetcher.ENGINE.toByteArray(Charsets.UTF_8))
        fun walk(path: String) {
            val names = assets.list(path).orEmpty().sorted()
            if (names.isEmpty()) {
                AssetDigest.update(md, assets, path)
            } else {
                for (name in names) walk("$path/$name")
            }
        }
        walk("ondevice")
        // Earlier markers could survive an interrupted same-APK mod rebuild.
        return "2|" + md.digest().joinToString("") { "%02x".format(it) }.take(16)
    }

    private fun haveOutputs(pkg: File): Boolean {
        val data = File(pkg, "data.apk")
        val engine = File(pkg, "lib/arm64/libil2cpp.so")
        return data.isFile && data.length() > 0 && engine.isFile && engine.length() > 0
    }

    fun isReady(pkg: File, signature: String): Boolean {
        val marker = marker(pkg)
        if (!haveOutputs(pkg) || !marker.isFile) return false
        return try {
            marker.readText() == signature
        } catch (e: IOException) {
            LauncherLog.log("Could not read the installed build marker", e)
            false
        }
    }

    /** Must succeed before a build can change any installed artifact. */
    fun invalidate(pkg: File) {
        val marker = marker(pkg)
        if (marker.exists() && !marker.delete()) {
            throw IOException("Could not invalidate the installed build marker")
        }
    }

    /** Called after the engine, image, content and installed mod records all succeed. */
    fun complete(pkg: File, signature: String) {
        if (!haveOutputs(pkg)) throw IOException("The installed engine or player image is missing")
        writeAtomic(marker(pkg), signature)
    }

    internal fun writeAtomic(file: File, text: String) {
        val parent = file.parentFile
        if (parent != null && !parent.isDirectory && !parent.mkdirs()) {
            throw IOException("Could not create $parent")
        }
        val tmp = File(parent, "${file.name}.part")
        FileOutputStream(tmp).use { output ->
            output.write(text.toByteArray(Charsets.UTF_8))
            output.fd.sync()
        }
        Files.move(
            tmp.toPath(), file.toPath(),
            StandardCopyOption.ATOMIC_MOVE, StandardCopyOption.REPLACE_EXISTING,
        )
    }
}
