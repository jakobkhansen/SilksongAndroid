// DepotLocation — where the game's own files are, and how the user says so.
//
// The app's own external directory is still the answer for a download, and
// still the first place a hand-copied depot is looked for. What this adds is a
// second answer: a folder the user picked, anywhere on the device.
//
// Why that needs saying at all is that a picked folder cannot be a Storage
// Access Framework URI. Everything downstream of here wants a real path:
//
//   * the catalog is repointed at <files>/aa, a symlink to the depot's
//     StreamingAssets/aa, and the engine open()s bundles through it from
//     native code that has never heard of a content provider;
//   * retargetContent rewrites all ~2000 bundles IN PLACE, by handing
//     absolute paths to bundle-surgery, which is a .NET process;
//   * PlayerImage.depotData and both build stamps walk the tree with File.
//
// So the picker is UI and nothing more: the tree URI it returns is resolved to
// a path, and the path is what is kept. That works because the app targets SDK
// 28 -- forced by SELinux, so that the fetched clang may exec at all (see
// tools/depot-to-apk/build.sh) -- and an app targeting below 29 is given the
// legacy storage view, which is plain file access to the whole volume once
// READ_EXTERNAL_STORAGE is granted. The two constraints happen to agree.
//
// Downloads deliberately do NOT go to a picked folder. DepotFetcher.dropUnwritten
// deletes zero-block files anywhere under the directory it is downloading into,
// which is right in a directory that only ever holds our download and very
// wrong in a folder of someone's own making.
//
// The chosen path is kept in a file rather than in preferences, for two
// reasons: the game runs in another process and cannot read our preferences,
// and BuildReset clears the launcher's preferences, which must not cost the
// user an eight gigabyte copy.

package dev.silksong.launcher

import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Environment
import android.provider.DocumentsContract
import java.io.File

object DepotLocation {

    /**
     * Where the chosen path is written, under the app's external files dir.
     *
     * The same directory the game reads its settings from, and for the same
     * reason: it is what Unity reports as the persistent data path, so the
     * game side needs no package name and no path convention. Not in
     * BuildReset's list, so a reset keeps it.
     */
    private const val POINTER = "depot-path.txt"

    /**
     * Where the content tree actually is, written for the game's process.
     *
     * The game runs in its own process and re-makes the <files>/aa link when
     * it finds it missing -- internal storage can be cleared without anything
     * about the build changing. It cannot search for the depot the way the
     * launcher does, so the launcher leaves it the answer, resolved.
     *
     * Separate from POINTER because it names the tree rather than the folder
     * above it, and because it is written on every build rather than only when
     * somebody picks something.
     */
    private const val CONTENT_POINTER = "content-path.txt"

    /** Left in a picked folder, because the folder now belongs to the game. */
    private const val MARKER = "SILKSONG-DO-NOT-DELETE.txt"

    /**
     * The only authority whose tree URIs map to a path.
     *
     * A USB stick or a network share arrives through some other provider, and
     * for those there is no path to resolve and nothing this can do.
     */
    private const val EXTERNAL_STORAGE = "com.android.externalstorage.documents"

    /** What legacy file access is granted by. Both, so the retarget can write. */
    val PERMISSIONS = arrayOf(
        android.Manifest.permission.READ_EXTERNAL_STORAGE,
        android.Manifest.permission.WRITE_EXTERNAL_STORAGE,
    )

    fun hasPermission(context: Context): Boolean = PERMISSIONS.all {
        context.checkSelfPermission(it) == android.content.pm.PackageManager.PERMISSION_GRANTED
    }

    // ── where to look ──────────────────────────────────────────────────────

    /**
     * Everywhere the game's files might be, best first.
     *
     * The picked folder leads, and the app's own external directories follow
     * unchanged -- one per volume, which is what makes a copy onto an SD card
     * work. An install that predates any of this has no pointer file, so the
     * list is exactly what it always was and nothing about it is rebuilt.
     */
    fun candidates(context: Context): List<File> =
        listOfNotNull(picked(context)) + appDirs(context)

    /** The app's own depot directory on each volume. Where a download goes. */
    fun appDirs(context: Context): List<File> =
        context.getExternalFilesDirs(null).filterNotNull().map { File(it, "depot") }

    /**
     * The depot to use, or the primary app directory when there is none.
     *
     * Whichever candidate actually holds the game wins, so a picked folder
     * that has been emptied falls back to the app's own directory rather than
     * taking the whole app down with it.
     */
    fun resolve(context: Context): File? =
        candidates(context).firstOrNull { PlayerImage.depotData(it) != null }
            ?: appDirs(context).firstOrNull()

    /**
     * Where a Steam download is written. Always ours, never the user's.
     *
     * See the header: the resume path deletes files it did not write, which is
     * only ever safe inside a directory that holds nothing else.
     */
    fun downloadTarget(context: Context): File? = appDirs(context).firstOrNull()

    /** True when the game's files are somewhere this app can reach them. */
    fun present(context: Context): Boolean =
        candidates(context).any { PlayerImage.depotData(it) != null }

    // ── the pointer ────────────────────────────────────────────────────────

    private fun pointerFile(context: Context): File? =
        context.getExternalFilesDir(null)?.let { File(it, POINTER) }

    /** The folder the user picked, if they have picked one. */
    fun picked(context: Context): File? {
        val f = pointerFile(context)?.takeIf { it.isFile } ?: return null
        val path = runCatching { f.readText().trim() }.getOrNull().orEmpty()
        return path.takeIf { it.isNotEmpty() }?.let(::File)
    }

    /**
     * Remembers a picked folder, for this process and for the game's.
     *
     * Written whole and atomically: a half-written path is a path to nowhere,
     * and the game process reads this without any way to ask again.
     */
    fun remember(context: Context, dir: File) {
        val out = pointerFile(context) ?: return
        val tmp = File(out.parentFile, "$POINTER.part")
        try {
            tmp.writeText(dir.absolutePath)
            if (!tmp.renameTo(out)) {
                tmp.delete()
                throw java.io.IOException("rename to $out")
            }
            LauncherLog.log("depot folder: $dir")
        } catch (t: Throwable) {
            LauncherLog.log("could not record the depot folder", t)
        }
    }

    /**
     * Forgets where the game is, without touching the game.
     *
     * Both pointers, because they are one fact written twice: a depot path
     * this process resolves from, and the content path the game's process
     * repairs its link from. Leaving the second would have the game linking
     * into a folder the launcher no longer knows about.
     *
     * Only ever the app's memory of the folder. The folder and its several
     * gigabytes are the user's own and are not this function's business -- and
     * a depot the app downloaded itself is not affected at all, since that one
     * is found by looking rather than by being remembered.
     */
    fun forget(context: Context) {
        pointerFile(context)?.delete()
        context.getExternalFilesDir(null)?.let { File(it, CONTENT_POINTER).delete() }
    }

    /**
     * Points the game's process at the content tree, and links it here.
     *
     * Both together, because they are the same fact written twice: the link is
     * what the catalog resolves through, and the pointer is how the game's own
     * process rebuilds that link if it ever has to. Called before every launch
     * as well as at the end of a build, so that moving the game to a different
     * folder does not need a rebuild to take effect.
     */
    fun relink(context: Context, depot: File) {
        PlayerImage.linkContent(context.filesDir, depot)
        val content = contentDir(depot) ?: return
        val out = context.getExternalFilesDir(null)?.let { File(it, CONTENT_POINTER) } ?: return
        try {
            if (out.isFile && out.readText().trim() == content.absolutePath) return
            val tmp = File(out.parentFile, "$CONTENT_POINTER.part")
            tmp.writeText(content.absolutePath)
            if (!tmp.renameTo(out)) {
                tmp.delete()
                throw java.io.IOException("rename to $out")
            }
            LauncherLog.log("content: $content")
        } catch (t: Throwable) {
            // The link above is what the game reads; this only helps it repair
            // that link on its own, so a failure here is not fatal.
            LauncherLog.log("could not record the content path", t)
        }
    }

    // ── picking one ────────────────────────────────────────────────────────

    /**
     * The system folder picker, opened somewhere useful.
     *
     * Without a starting point it opens at the volume root, which is one of
     * the few places Android refuses to grant -- so the first thing the user
     * sees is "Can't use this folder" in red, on a screen they were sent to by
     * a button that promised to work. Documents is a real folder, is not on
     * the refused list (the root, Download, and other apps' data are), and is
     * a plausible place to have put eight gigabytes. It is only where the
     * picker starts; anywhere reachable from there is still pickable.
     */
    fun pickIntent(): Intent =
        Intent(Intent.ACTION_OPEN_DOCUMENT_TREE).apply {
            addFlags(
                Intent.FLAG_GRANT_READ_URI_PERMISSION or
                    Intent.FLAG_GRANT_WRITE_URI_PERMISSION or
                    Intent.FLAG_GRANT_PERSISTABLE_URI_PERMISSION,
            )
            putExtra(
                DocumentsContract.EXTRA_INITIAL_URI,
                DocumentsContract.buildDocumentUri(EXTERNAL_STORAGE, "primary:Documents"),
            )
        }

    /**
     * The real path behind a tree URI, or null when there is not one.
     *
     * A document id is "<volume>:<path below it>". "primary" is the emulated
     * internal volume; anything else is a volume UUID, which is mounted under
     * /storage by that name. Nothing is verified here -- the caller is better
     * placed to say why a folder is not usable than a null would be.
     */
    fun pathFor(uri: Uri): File? {
        if (uri.authority != EXTERNAL_STORAGE) return null
        val id = runCatching { DocumentsContract.getTreeDocumentId(uri) }.getOrNull() ?: return null
        val colon = id.indexOf(':')
        val volume = if (colon < 0) id else id.substring(0, colon)
        val relative = if (colon < 0) "" else id.substring(colon + 1)
        val root = when {
            volume.equals("primary", ignoreCase = true) ->
                Environment.getExternalStorageDirectory() ?: return null
            // The provider's own name for the Documents folder.
            volume.equals("home", ignoreCase = true) ->
                File(Environment.getExternalStorageDirectory() ?: return null, "Documents")
            else -> File("/storage/$volume")
        }
        return if (relative.isEmpty()) root else File(root, relative)
    }

    /**
     * Why a folder cannot be used, or null when it can.
     *
     * Both halves matter and they fail differently. Unreadable is usually a
     * permission that was refused; unwritable matters because the content
     * retarget rewrites every bundle in place, so a read-only depot fails
     * minutes into a build rather than here.
     *
     * Asked rather than assumed, and that is the point. A removable card is
     * the obvious suspect -- the platform has spent years narrowing what an
     * app may do with one -- but a card on a legacy-storage app is not
     * necessarily read-only: verified on an AYN Thor (Android 13), where the
     * app wrote its own marker into Documents on the SD card without trouble.
     * Refusing cards by name would have turned a working setup into an error
     * message, so the probe decides and the device answers for itself.
     */
    fun problemWith(dir: File): String? {
        if (!dir.isDirectory) {
            return "that folder cannot be read. If it is on a memory card or a USB stick, " +
                "try a folder in internal storage instead."
        }
        if (PlayerImage.depotData(dir) == null) {
            return "no game files in there: ${PlayerImage.depotProblem(dir)}.\n\n" +
                "What has to be in the folder is \"Hollow Knight Silksong_Data\" and " +
                "everything beside it, from the game's Linux files."
        }
        PlayerImage.wrongPlatform(dir)?.let { return it }
        if (!isWritable(contentDir(dir) ?: dir)) {
            return "that folder cannot be written to, and the game's content has to be " +
                "converted in place. Some devices keep memory cards read-only for apps; " +
                "if that folder is on one, copy the game to internal storage instead."
        }
        return null
    }

    /** The Addressables tree, which is what the retarget rewrites. */
    private fun contentDir(depot: File): File? =
        PlayerImage.depotData(depot)?.let { File(it, "StreamingAssets/aa") }?.takeIf { it.isDirectory }

    /**
     * Whether we may write in a directory, asked by writing in it.
     *
     * canWrite() answers from the mode bits, which say yes on a card the
     * kernel will refuse anyway, so the only reliable question is the real one.
     */
    private fun isWritable(dir: File): Boolean = try {
        val probe = File(dir, ".silksong-write-probe")
        probe.delete()
        val ok = probe.createNewFile()
        probe.delete()
        ok
    } catch (t: Throwable) {
        false
    }

    /**
     * Says, in the folder itself, that the folder is now load-bearing.
     *
     * The depot is not a copy of anything after this: only the 55 MB player
     * image is taken inside the app, and the several gigabytes of content stay
     * here and are read from here every time the game runs. Somebody tidying
     * up their downloads a month later has no other way to know that.
     */
    fun writeMarker(dir: File) {
        try {
            File(dir, MARKER).writeText(
                "Silksong Android is using this folder.\n" +
                    "\n" +
                    "The game reads its content straight out of here every time it runs -- " +
                    "it was not copied into the app, because it is several gigabytes.\n" +
                    "\n" +
                    "Deleting or moving this folder will stop the game from starting, and " +
                    "the app will not be able to update it either. Both mean downloading " +
                    "the game again and rebuilding it, which takes around half an hour.\n",
            )
        } catch (t: Throwable) {
            // Advisory. A folder that will not take a text file is worth a log
            // line and nothing more.
            LauncherLog.log("could not leave a marker in $dir: ${t.message}")
        }
    }
}
