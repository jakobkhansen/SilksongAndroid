// BuildReset — throw away the ported game, keep everything expensive.
//
// A port can end up wrong in ways nothing detects: a build interrupted at the
// wrong moment, a patch that changed under a stamp, an engine that loads and
// then misbehaves. The answer was to uninstall the app, which also threw away
// the depot -- around 8 GB fetched from Steam -- and the player's saves. That
// is a bad trade for "try building it again".
//
// So this deletes what the build PRODUCED and nothing that was DOWNLOADED:
//
//   gone    build/          the on-device build: generated C++, objects,
//                           libil2cpp.so, the player image, and the stamps
//                           that say those are current
//           pkg/            the packaged player the engine actually loads
//           unity-dex/      the player's Java classes, dexed; seconds to redo
//           aa              the symlink to the content tree, remade every run
//           staging/        transient, and only ever holds a partial copy
//           depot-staging/
//           the game's settings   volumes, language, brightness, resolution,
//                           frame cap -- and the marker that says the
//                           first-run resolution has been decided
//           the launcher's settings
//           the remembered depot folder   the PATH ONLY. See clear() for why.
//
//   kept    depot/          the game, ~8 GB, and the user's own property
//           the game's files wherever the user put them, untouched
//           unity/          Unity's Android player module, ~640 MB
//           toolchain/      clang and .NET, several hundred MB
//           default/        THE SAVES
//           the Steam sign-in
//
// The kept list is the point. Everything in it is either expensive to fetch
// again or impossible to replace, and none of it is what a bad port is made
// of -- the depot is verified against Steam's manifest, and the toolchain
// against a marker written only after a real compile and link succeeded.
//
// The sign-in is kept for the same reason: it is a QR scan on a phone, it has
// nothing to do with how the game was built, and losing it is a worse
// interruption than the one being fixed. It also lives in its own file, is
// encrypted with a key from the Android Keystore, and is the only thing here
// that cannot be reconstructed from what is on disk.
//
// Anything not named here is left alone. Directories from older layouts of
// this app are still on some devices, and deleting a directory because it is
// unfamiliar is how a feature meant to save a download costs one.

package dev.silksong.launcher

import android.content.Context
import java.io.File

object BuildReset {

    /** Under the app's external files dir. */
    private val EXTERNAL = listOf("build", "staging", "depot-staging")

    /** Under filesDir, on internal storage. */
    private val INTERNAL = listOf("pkg", "unity-dex", "aa")

    /**
     * Deletes the build. Returns roughly how many bytes it freed.
     *
     * Slow -- the build is gigabytes across thousands of small files -- so
     * this must not be called on the main thread.
     */
    fun clear(context: Context): Long {
        BuildInstallation.invalidate(File(context.filesDir, "pkg"))
        var freed = 0L

        val external = context.getExternalFilesDir(null)
        if (external != null) {
            for (name in EXTERNAL) freed += remove(File(external, name))
        } else {
            LauncherLog.log("clear: no external files dir; the build may be left behind")
        }

        for (name in INTERNAL) freed += remove(File(context.filesDir, name))

        // Where the game is, but never the game. A reset is what someone does
        // when the port came out wrong, and one of the things that can be
        // wrong is the copy it was built from -- an interrupted transfer, or
        // the wrong folder picked. Keeping the path would rebuild from exactly
        // the same files without ever asking; forgetting it puts the question
        // back on screen, with the folder still sitting there to be chosen
        // again. Costs a few taps and nothing else: a depot this app
        // downloaded itself is found by looking, so it is unaffected.
        DepotLocation.forget(context)

        clearPreferences(context)

        LauncherLog.log("clear: freed ${freed / 1024 / 1024} MB; the game will be built again")
        return freed
    }

    /**
     * Deletes one Unity engine and everything built from it, so that the
     * engine this app pins can be fetched and the game built against that.
     *
     * Narrower than [clear], because nobody asked for this: it runs once after
     * an app update that changes [UnityFetcher.ENGINE]. So it keeps everything
     * that does not depend on the engine -- the settings, the remembered depot
     * folder and the retargeted content, which is minutes of rewriting bundles
     * to arrive at the same bytes -- and takes everything that does:
     *
     *   gone    unity/          the old engine, IL2CPP and BCL, but not the
     *                           Input System sources: those are pinned by
     *                           their own version and digest
     *           build/          the conversion, objects, libil2cpp.so, the
     *                           player image, compiled patches and packages,
     *                           and the stamps that call them current
     *           staging/        engine libraries waiting to be installed
     *           pkg/            the installed engine and player image
     *           unity-dex/      the old engine's Java classes
     *
     *   kept    build/content.stamp, and everything [clear] keeps
     *
     * All of it has to go together. The steps that check whether they are
     * current look at timestamps, counts and sizes, none of which say which
     * engine a file came from -- and the new engine's runtime sources unpack
     * with Unity's own, older, dates -- so a stale object or dex survives a
     * rebuild that does not start from nothing.
     */
    fun clearEngine(context: Context, unity: File): Long {
        BuildInstallation.invalidate(File(context.filesDir, "pkg"))
        var freed = removeChildren(unity, keep = UNITY_KEEP)

        val external = context.getExternalFilesDir(null)
        if (external != null) {
            freed += removeChildren(File(external, "build"), keep = BUILD_KEEP)
            freed += remove(File(external, "staging"))
        } else {
            LauncherLog.log("clear: no external files dir; the old engine's build may be left behind")
        }

        for (name in listOf("pkg", "unity-dex")) freed += remove(File(context.filesDir, name))

        LauncherLog.log("clear: freed ${freed / 1024 / 1024} MB of the previous engine")
        return freed
    }

    /** Engine-independent, under unity/. */
    private val UNITY_KEEP = setOf("packages")

    /** Engine-independent, under build/: see [PlayerImage.retargetContent]. */
    private val BUILD_KEEP = setOf("content.stamp")

    private fun removeChildren(dir: File, keep: Set<String>): Long {
        var freed = 0L
        for (child in dir.listFiles().orEmpty()) {
            if (child.name !in keep) freed += remove(child)
        }
        return freed
    }

    /**
     * Resets the game's settings and the launcher's, but not the sign-in.
     *
     * Through the SharedPreferences API rather than by deleting the files, and
     * that is not a stylistic choice. Android keeps a crash-recovery journal
     * beside each preferences file, "<name>.xml.bak", and restores it over the
     * real one on the next load -- so a file deleted behind the API's back can
     * simply come back, with the values that were being reset. (Learned the
     * hard way: a copy made as a "backup" was restored over the edit it was
     * meant to protect.) The API also updates the in-memory copy, so nothing
     * writes the old values out again at process exit.
     *
     * The game's file is Unity's PlayerPrefs, which it names after the package.
     * It holds volumes, language, brightness, the video settings and the marker
     * that says the first-run resolution has been chosen -- so clearing it is
     * what makes a re-port feel like a first one. It does NOT hold saves; those
     * are files under the external files dir and are not touched by any of this.
     */
    private fun clearPreferences(context: Context) {
        // Unity's own naming. Matches the file seen on device:
        // shared_prefs/<package>.v2.playerprefs.xml
        clear(context, "${context.packageName}.v2.playerprefs", "the game's settings")
        clear(context, "launcher_settings", "the launcher's settings")
    }

    private fun clear(context: Context, name: String, what: String) {
        try {
            val prefs = context.applicationContext
                .getSharedPreferences(name, Context.MODE_PRIVATE)
            // commit, not apply: this is followed by starting the port, and an
            // asynchronous write racing a process restart is how a reset that
            // reported success comes back.
            val ok = prefs.edit().clear().commit()
            LauncherLog.log("clear: ${if (ok) "reset" else "could not reset"} $what")
        } catch (t: Throwable) {
            LauncherLog.log("clear: could not reset $what: ${t.message}")
        }
    }

    /** True once there is something worth clearing. */
    fun hasBuild(context: Context): Boolean {
        if (File(File(context.filesDir, "pkg"), ".built").isFile) return true
        val external = context.getExternalFilesDir(null) ?: return false
        return File(external, "build").isDirectory
    }

    private fun remove(target: File): Long {
        val tally = Tally()
        deleteTree(target, tally)
        if (tally.bytes > 0 || tally.failures > 0) {
            LauncherLog.log(
                "clear: ${if (tally.failures == 0) "removed" else "could not fully remove"} " +
                    "${target.name} (${tally.bytes / 1024 / 1024} MB" +
                    if (tally.failures > 0) ", ${tally.failures} left behind)" else ")",
            )
        }
        return tally.bytes
    }

    /** Carried down the walk, so nothing is accumulated in shared state. */
    private class Tally {
        var bytes = 0L
        var failures = 0
    }

    /**
     * Deletes a tree WITHOUT ever following a symbolic link.
     *
     * File.deleteRecursively cannot be used here, and the reason is not
     * theoretical. filesDir/aa is a link to the depot's StreamingAssets/aa
     * (PlayerImage.linkContent), and a walker that lists a linked directory
     * gets the TARGET's children -- so deleting the link recursively deletes
     * several gigabytes of the game this is trying not to touch. Older builds
     * have the same shape inside build/, where usr and sysroot were links into
     * the toolchain on devices whose filesystem allowed it.
     *
     * So every entry is lstat'ed, and a link is unlinked rather than entered.
     */
    private fun deleteTree(target: File, tally: Tally) {
        val kind = kindOf(target) ?: return   // already gone

        when (kind) {
            // Unlink. Whatever it pointed at is left exactly as it was.
            Kind.Link -> if (!target.delete()) tally.failures++

            Kind.Directory -> {
                val children = target.listFiles()
                if (children != null) for (child in children) deleteTree(child, tally)
                if (!target.delete()) tally.failures++
            }

            Kind.File -> {
                val size = target.length()
                if (target.delete()) tally.bytes += size else tally.failures++
            }
        }
    }

    private enum class Kind { File, Directory, Link }

    /**
     * What this path is, without resolving it.
     *
     * lstat rather than File.isDirectory, because isDirectory answers for the
     * link's TARGET and so calls a link to a directory a directory. Null means
     * the path does not exist.
     */
    private fun kindOf(f: File): Kind? =
        try {
            val mode = android.system.Os.lstat(f.absolutePath).st_mode
            when {
                android.system.OsConstants.S_ISLNK(mode) -> Kind.Link
                android.system.OsConstants.S_ISDIR(mode) -> Kind.Directory
                else -> Kind.File
            }
        } catch (e: android.system.ErrnoException) {
            null
        }
}
