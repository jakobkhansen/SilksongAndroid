// LauncherLog — single global event log for the launcher UI. Any
// component (SteamSession, LauncherActivity itself)
// can append lines via [log]; LauncherActivity subscribes via
// [setListener] and mirrors them into the on-screen log panel.
//
// Why a manual pubsub instead of LiveData/Flow: we want this object
// reachable from anywhere (including non-Android code paths like
// background coroutines + JavaSteam callbacks) without forcing every
// caller to depend on lifecycle libraries. The total event volume is
// small (~10s of lines per session) so a CopyOnWriteArrayList is fine.
//
// NOTHING IDENTIFYING GOES IN HERE. Not the Steam account name, not a
// SteamID, not a token. This log is written to files that survive the
// process -- one of them on shared storage, where other software on the phone
// may be able to read it -- the log screen offers Share, Save and Copy, and
// the whole reason it exists is for people to send it to us when a build
// fails. So every line should be read as something the user is about to post
// in an issue. Say that a sign-in happened, which is what makes the log
// useful; say who it was for, and the user has published their Steam account
// name to get help with a compiler error.
//
// The account name still belongs on SCREEN, where it tells the person which
// account they are signed in as and goes no further: LauncherActivity's login
// status line and LoginActivity's success message both show it.

package dev.silksong.launcher

import android.content.Context
import android.util.Log
import java.io.File
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import java.util.concurrent.CopyOnWriteArrayList

object LauncherLog {
    private const val TAG = "SilksongLauncher"
    private const val MAX_LINES = 500

    // Newest at the end. Read-only snapshot returned to subscribers.
    private val buffer = CopyOnWriteArrayList<String>()
    private val listeners = CopyOnWriteArrayList<Listener>()
    private val timeFormat = SimpleDateFormat("HH:mm:ss", Locale.US)

    // ── the files ───────────────────────────────────────────────────────────
    //
    // The buffer above is memory, and memory is exactly what a bug report does
    // not have: the interesting failures happen during setup, the user closes
    // the app, and by the time anybody asks for the log it is gone. So every
    // line is also appended to a file, and the file is what the log screen
    // shows -- including the session that failed yesterday.
    //
    // Appended and closed per line rather than held open behind a buffer. The
    // whole point is to survive the process dying, and a buffered writer's
    // last few kilobytes are the ones that say why it died.
    //
    // Written TWICE, to two directories, because the two have different jobs:
    //
    //   filesDir            /data/data/<pkg>/files/launcher.log
    //                       Always writable, never unmounted, invisible to the
    //                       person holding the phone -- nothing on a stock
    //                       device can open /data/data. This is the copy the
    //                       log screen reads.
    //
    //   getExternalFilesDir /sdcard/Android/data/<pkg>/files/launcher.log
    //                       May be missing, and on Android 11+ is not
    //                       browsable by a third-party file manager, but it IS
    //                       reachable over USB and by anything with storage
    //                       access -- and it is the first place someone
    //                       looking for a log file thinks to look, which is
    //                       reason enough on its own.
    //
    // BuildReset leaves this file alone: it names the directories it deletes,
    // and the log sits beside them rather than in one. A log is at its most
    // useful directly after the reset that a failed build provoked.

    private const val LOG_NAME = "launcher.log"
    private const val MAX_FILE_BYTES = 256L * 1024L
    private const val MAX_FILE_LINES = 4000
    private val fileLock = Any()
    @Volatile private var sinks: List<File> = emptyList()

    /** Bytes appended by this process since [attach], for the trim below. */
    private var written = 0L

    /** Where the log lives, once [attach] has been called. */
    fun file(context: Context): File = File(context.filesDir, LOG_NAME)

    /** The copy a user can actually get at, when there is external storage. */
    fun externalFile(context: Context): File? =
        context.getExternalFilesDir(null)?.let { File(it, LOG_NAME) }

    /**
     * Starts writing to disk. Safe to call more than once and from any process.
     */
    fun attach(context: Context) {
        synchronized(fileLock) {
            if (sinks.isNotEmpty()) return
            val header = "\n=== ${SimpleDateFormat("yyyy-MM-dd HH:mm:ss", Locale.US).format(Date())} ===\n" +
                "${deviceSummary()}\n"
            val opened = ArrayList<File>(2)
            for (f in listOfNotNull(file(context), externalFile(context))) {
                try {
                    f.parentFile?.mkdirs()
                    trim(f)
                    f.appendText(header)
                    opened.add(f)
                } catch (t: Throwable) {
                    // External storage can be absent, full, or read-only, and
                    // none of that may cost us the internal copy.
                    Log.w(TAG, "could not open the log file at $f", t)
                }
            }
            if (opened.isEmpty()) Log.w(TAG, "no log file could be opened; this session is logcat only")
            // Seeded from what is already there: the previous sessions still in
            // the file count against the cap, or a process that starts at 255 KB
            // writes another 256 before anything trims it.
            written = opened.firstOrNull()?.length() ?: 0L
            sinks = opened
        }
    }

    /**
     * Caps a log file by dropping the oldest lines. Call under [fileLock].
     *
     * Bounded by BYTES first and lines second, and cut back to half the cap
     * rather than to the cap. Trimming to exactly the limit would leave the
     * next line over it again, and this function reads and rewrites the whole
     * file -- doing that once per logged line, during the build that is
     * producing them, is its own outage.
     *
     * The line cap is generous where the in-memory [MAX_LINES] is not: that
     * one backs a panel on a screen, this one backs the thing a user is asked
     * to send, and a port that failed an hour in did not fail in its last 500
     * lines.
     */
    private fun trim(f: File) {
        if (!f.isFile || f.length() <= MAX_FILE_BYTES) return
        val budget = MAX_FILE_BYTES / 2
        val keep = ArrayDeque<String>()
        var bytes = 0L
        for (line in f.readLines().asReversed()) {
            bytes += line.length + 1
            if (bytes > budget || keep.size >= MAX_FILE_LINES) break
            keep.addFirst(line)
        }
        f.writeText(keep.joinToString("\n", postfix = "\n"))
    }

    /** Everything on disk, which is more than this session. */
    fun history(context: Context): String =
        try {
            file(context).takeIf { it.isFile }?.readText().orEmpty()
        } catch (t: Throwable) {
            ""
        }

    /**
     * The device, in one line.
     *
     * First thing in every log and first thing in every report, because it is
     * the question asked of every bug that only happens to somebody else.
     */
    fun deviceSummary(): String =
        try {
            val pageSize = android.system.Os.sysconf(android.system.OsConstants._SC_PAGESIZE)
            "device: ${android.os.Build.MANUFACTURER} ${android.os.Build.MODEL}, " +
                "Android ${android.os.Build.VERSION.RELEASE} (API ${android.os.Build.VERSION.SDK_INT}), " +
                "abis=${android.os.Build.SUPPORTED_ABIS.joinToString("/")}, " +
                "pageSize=$pageSize"
        } catch (t: Throwable) {
            "device: unknown (${t.message})"
        }

    fun interface Listener {
        fun onLog(line: String, full: List<String>)
    }

    fun log(message: String) {
        val stamped = "[${timeFormat.format(Date())}] $message"
        Log.i(TAG, message)
        buffer.add(stamped)
        // Trim if needed. CopyOnWriteArrayList is O(n) on remove but
        // n stays small (capped at 500).
        while (buffer.size > MAX_LINES) buffer.removeAt(0)

        val out = sinks
        if (out.isNotEmpty()) {
            // A log line must never be the reason something fails, so a full
            // disk or a revoked directory is swallowed rather than thrown.
            try {
                synchronized(fileLock) {
                    val line = "$stamped\n"
                    for (f in out) f.appendText(line)

                    // Enforced on the way out and not only at attach(). A port
                    // runs for the better part of an hour and emits a
                    // compiler's worth of output, all of it in ONE process --
                    // so a cap applied only at startup is no cap at all, and
                    // the file the log screen has to render grows to whatever
                    // that build felt like saying. Which is how a log screen
                    // becomes a crash.
                    written += line.length
                    if (written > MAX_FILE_BYTES) {
                        for (f in out) trim(f)
                        written = out.firstOrNull()?.length() ?: 0L
                    }
                }
            } catch (_: Throwable) {
            }
        }

        val snapshot = buffer.toList()
        for (l in listeners) {
            try {
                l.onLog(stamped, snapshot)
            } catch (_: Throwable) {
                // Listener crashes shouldn't bring down whatever thread is logging.
            }
        }
    }

    /**
     * Logs a failure with its stack trace.
     *
     * The on-screen panel still gets one line -- it has no room for more --
     * but logcat gets the whole thing. A message alone is not enough to place
     * a failure inside a fetch that has a dozen steps and several archive
     * formats: "not in gzip format" is the same sentence wherever it came
     * from, and the frames are the only thing that says which one it was.
     */
    fun log(message: String, error: Throwable) {
        Log.w(TAG, message, error)
        log("$message: $error")
    }

    fun snapshot(): List<String> = buffer.toList()

    fun addListener(l: Listener) {
        listeners.add(l)
    }

    fun removeListener(l: Listener) {
        listeners.remove(l)
    }
}
