// Toolchain — running fetched native programs on the device.
//
// The game is compiled here, on the phone, which means this app has to run a
// compiler: several hundred megabytes of clang, lld and .NET that arrive after
// installation and therefore live in storage the app writes to itself. Android
// does not make that easy, and the two rules below are the whole reason this
// file exists rather than a bare ProcessBuilder call at each site.
//
// **Executables must be on internal storage.** External storage is a FUSE
// mount (sdcardfs / MediaProvider) with no execute permission and no way to
// set one -- chmod +x there silently does nothing. So the depot, the generated
// C++ and the object files can live on /sdcard, where there is room, but
// anything that is exec()'d has to be under filesDir.
//
// **The app must target API 28.** Android picks a process's SELinux domain
// from targetSdkVersion; only untrusted_app_27 (targetSdk 26-28) carries
//
//     allow untrusted_app_27 app_data_file:file execute_no_trans;
//
// At 29 and above every exec of a file the app wrote fails with EACCES no
// matter what its mode bits say. tools/depot-to-apk/build.sh pins it, and
// `probe` below verifies it rather than trusting that it stayed pinned -- a
// silent regression here would surface as an unreadable failure twenty minutes
// into a build.

package dev.silksong.launcher

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.withContext
import java.io.BufferedReader
import java.io.File
import java.io.InputStreamReader
import java.util.concurrent.TimeUnit
import kotlin.coroutines.coroutineContext

object Toolchain {

    /**
     * Everything executable lives here, under internal storage.
     *
     * Deliberately not getExternalFilesDir: see the header. This directory is
     * on the same physical volume on most devices, so the choice costs no
     * space -- it buys the execute permission.
     */
    fun rootFor(context: android.content.Context): File = File(context.filesDir, "toolchain")

    /** Where the compiler's own libraries end up, for LD_LIBRARY_PATH. */
    fun libDirs(root: File): List<File> = listOf(File(root, "usr/lib"))

    /**
     * The environment a fetched program needs to run.
     *
     * Three things, none of them optional:
     *
     * LD_LIBRARY_PATH, because Termux builds its binaries with a RUNPATH of
     * /data/data/com.termux/files/usr/lib, which does not exist here. Without
     * it clang dies before main with "library libclang-cpp.so not found".
     *
     * TMPDIR, because Android has no /tmp at all and clang writes the object
     * file for every compile through one. Its complaint is "unable to make
     * temporary file: No such file or directory", which reads like a problem
     * with the output path rather than with a directory nobody mentioned.
     *
     * PATH, so that the driver finds its own assembler and linker rather than
     * whatever Android ships in /system/bin.
     */
    fun environment(root: File): Map<String, String> {
        val tmp = File(root, "tmp").apply { mkdirs() }
        return mapOf(
            "LD_LIBRARY_PATH" to libDirs(root).joinToString(":") { it.absolutePath },
            "TMPDIR" to tmp.absolutePath,
            "PATH" to "${File(root, "usr/bin").absolutePath}:/system/bin",
            "HOME" to root.absolutePath,
        )
    }

    /**
     * The flags that make the fetched clang target Android rather than Termux.
     *
     * --unwindlib=none is the one that is not guessable. Termux's clang
     * defaults to its own libunwind, which is not in the NDK sysroot, so a
     * link that has otherwise gone perfectly ends with two copies of
     * "unable to find library -l:libunwind.a". The generated code does not
     * need it: -funwind-tables emits the tables, and the Android runtime
     * provides the personality routine.
     *
     * -resource-dir is deliberately *not* set. It carries the compiler's own
     * headers, arm_neon.h among them, and pointing clang 21 at the NDK's
     * clang 18 resource dir makes brotli's NEON intrinsics fail to compile.
     * Only the sysroot should come from the NDK.
     */
    fun targetFlags(root: File, api: Int = 33): List<String> = listOf(
        "--target=aarch64-linux-android$api",
        "--sysroot=${File(root, "sysroot").absolutePath}",
        "-stdlib=libc++",
        "--unwindlib=none",
    )

    class ExecException(message: String) : java.io.IOException(message)

    /**
     * Result of a finished process. Output is stdout and stderr interleaved,
     * as the program produced them -- keeping them apart loses the ordering,
     * and for a compiler the ordering is most of the diagnostic.
     *
     * [outOfMemory] is set when the program did not fail so much as get taken:
     * the platform reclaimed its process. It is a separate field rather than
     * something a caller reads out of [output] because it decides whether a
     * retry is worth making, and a decision that turns on matching a sentence
     * is a decision that breaks the next time the sentence is reworded.
     */
    data class Result(val code: Int, val output: String, val outOfMemory: Boolean = false) {
        val ok: Boolean get() = code == 0

        /**
         * Why this failed, in one line, for someone who will read it on a
         * phone and then paste it into an issue.
         *
         * [pick] finds the line a failing program leaves behind -- "error CS"
         * for a compiler, "error:" for clang. What matters here is what
         * happens when nothing matches, because the runs that fail without a
         * diagnostic are exactly the ones nobody can account for afterwards.
         *
         * Everything this side of the run writes plain English: monojni says
         * "the runtime failed to start", the launcher says "the build process
         * crashed", and neither contains the word "error". Filtering for that
         * word threw all of it away and left a bare number, and a bare number
         * is what came back to us -- "il2cpp failed after 5s: exit 120", from
         * a device we do not have, with the account of what happened sitting
         * in the log a line above. So what the run last said is used when
         * nothing matches, which is the death note when there is one: that
         * line is appended to the output for this.
         *
         * 120 is not a code any program chose. It is what the launcher
         * records for a run that ended without leaving one, so it is said in
         * words rather than printed as if il2cpp had returned it.
         */
        fun why(pick: (String) -> Boolean): String {
            val lines = output.lineSequence().map { it.trim() }.filter { it.isNotEmpty() }
            lines.firstOrNull(pick)?.let { return it }
            val last = lines.lastOrNull()
            return when {
                code != MonoService.FAILED -> last ?: "exit $code"
                last != null -> "no result from the build process, after \"$last\""
                else -> "no result from the build process"
            }
        }
    }

    /**
     * Runs a program and waits for it.
     *
     * [onLine] sees each line as it arrives, which is what makes a seventeen
     * minute compile watchable; the same lines are also accumulated into the
     * result, capped, so a failure can be reported without having had to keep
     * a listener attached.
     *
     * Cancelling the calling coroutine destroys the process. Without that a
     * cancelled build would leave clang running -- and on a phone that is
     * eight cores of battery drain with nothing left to collect the result.
     */
    suspend fun exec(
        argv: List<String>,
        cwd: File? = null,
        env: Map<String, String> = emptyMap(),
        onLine: (String) -> Unit = {},
    ): Result = withContext(Dispatchers.IO) {
        val pb = ProcessBuilder(argv)
        if (cwd != null) pb.directory(cwd)
        pb.redirectErrorStream(true)
        pb.environment().putAll(env)

        val process = try {
            pb.start()
        } catch (t: java.io.IOException) {
            // The message Android gives for a blocked exec ("Permission
            // denied") says nothing about why, and the why is never the file
            // mode. Say it here, once, rather than leaving it to be
            // rediscovered.
            throw ExecException(
                "could not run ${argv.firstOrNull()}: ${t.message}. " +
                    "Executables must be under filesDir (external storage is noexec) " +
                    "and the app must target API 28 or lower.",
            )
        }

        val collected = StringBuilder()
        // Cancelling has to reach the process, and interrupting the thread
        // will not: a read on a pipe does not respond to it. Closing the
        // process's output from underneath the reader does -- readLine then
        // returns and the loop falls out on its own.
        val onCancel = coroutineContext[Job]?.invokeOnCompletion {
            if (process.isAlive) process.destroyForcibly()
        }
        try {
            BufferedReader(InputStreamReader(process.inputStream)).use { r ->
                while (true) {
                    val line = r.readLine() ?: break
                    if (collected.length < MAX_CAPTURED) collected.append(line).append('\n')
                    onLine(line)
                }
            }
            Result(process.waitFor(), collected.toString())
        } finally {
            onCancel?.dispose()
            if (process.isAlive) {
                process.destroyForcibly()
                process.waitFor(5, TimeUnit.SECONDS)
            }
        }
    }

    private const val MAX_CAPTURED = 1 shl 20

    // ── proving the toolchain ──────────────────────────────────────────────

    /**
     * Compiles and links a small shared library, and checks the result is an
     * arm64 ELF.
     *
     * Worth the two seconds it takes. The alternative is discovering that a
     * package did not extract, or that the sysroot is missing the API level
     * being targeted, somewhere in the middle of a compile that runs for
     * seventeen minutes -- and the error when that happens is a missing header
     * a thousand lines into someone else's generated code, which says nothing
     * about the real cause.
     */
    suspend fun verify(root: File, api: Int = 33, onLine: (String) -> Unit = {}): String? {
        val clang = File(root, "usr/bin/clang++")
        if (!clang.canExecute()) return "clang++ is missing from ${root.name}"

        val dir = File(root, "verify").apply { mkdirs() }
        val src = File(dir, "probe.cpp")
        // Deliberately not a bare "int main". These are the pieces the game's
        // own build needs and that a partial fetch breaks: a C++ standard
        // header (libc++ from the NDK sysroot), a bionic header, and a link
        // against an Android system library.
        src.writeText(
            """
            #include <cmath>
            #include <string>
            #include <android/log.h>
            extern "C" int silksong_probe(int n) {
                std::string s = std::to_string(std::sqrt((double)n));
                __android_log_print(4, "probe", "%s", s.c_str());
                return (int)s.size();
            }
            """.trimIndent(),
        )
        val out = File(dir, "libprobe.so")
        out.delete()

        val argv = ArrayList<String>()
        argv += clang.absolutePath
        argv += targetFlags(root, api)
        argv += listOf("-fPIC", "-O1", "-shared", "-o", out.absolutePath, src.absolutePath, "-llog")
        val result = exec(argv, cwd = dir, env = environment(root), onLine = onLine)
        if (!result.ok) return result.output.trim().lines().firstOrNull() ?: "clang exited ${result.code}"

        val header = ByteArray(20)
        out.inputStream().use { if (it.read(header) < 20) return "the linker produced nothing usable" }
        val elf = header[0] == 0x7F.toByte() && header[1] == 'E'.code.toByte() &&
            header[2] == 'L'.code.toByte() && header[3] == 'F'.code.toByte()
        if (!elf) return "the linker's output is not an ELF file"
        // e_machine at offset 18, little endian. 0xB7 is EM_AARCH64.
        if (header[18] != 0xB7.toByte()) return "built for the wrong architecture"
        return null
    }

    // ── the probe ──────────────────────────────────────────────────────────

    /**
     * Whether this device will run a binary the app wrote itself.
     *
     * Answered by actually doing it, not by reading Build.VERSION: the
     * permission comes from the SELinux domain, which comes from
     * targetSdkVersion, and neither is visible through a public API. A device
     * with a modified policy would also be caught, and the answer is wanted
     * before a user is invited to start a multi-gigabyte download for a build
     * that cannot run.
     *
     * The result is cached in memory. It cannot change without the process
     * being replaced, since targetSdkVersion is fixed at install time.
     */
    @Volatile private var probed: Boolean? = null

    /**
     * The cached answer, or null when the probe has not run yet.
     *
     * For the UI, which asks on every refresh and cannot afford to fork a
     * process on the main thread to find out.
     */
    fun executeKnown(): Boolean? = probed

    fun canExecute(root: File): Boolean {
        probed?.let { return it }
        synchronized(this) {
            probed?.let { return it }
            val result = runCatching { probe(root) }.getOrElse {
                LauncherLog.log("Exec probe failed: $it")
                false
            }
            probed = result
            return result
        }
    }

    /**
     * Copies a known-good binary into app storage and runs it.
     *
     * toybox is used because every Android has one, it is a real dynamically
     * linked ELF -- so this exercises the loader as well as the exec -- and it
     * echoes back whatever it is given, which proves our copy ran rather than
     * something on PATH. It dispatches on argv[0], hence the name.
     */
    private fun probe(root: File): Boolean {
        val src = File("/system/bin/toybox")
        if (!src.canRead()) {
            LauncherLog.log("Exec probe: no /system/bin/toybox to test with")
            return false
        }
        val dir = File(root, "probe").apply { mkdirs() }
        val bin = File(dir, "toybox")
        if (bin.length() != src.length()) {
            src.inputStream().use { i -> bin.outputStream().use { o -> i.copyTo(o) } }
        }
        if (!bin.setExecutable(true, true)) {
            LauncherLog.log("Exec probe: could not set the execute bit on $bin")
            return false
        }
        val token = "silksong-exec-ok"
        val pb = ProcessBuilder(bin.absolutePath, "echo", token).redirectErrorStream(true)
        val p = pb.start()
        val out = p.inputStream.bufferedReader().use { it.readText() }
        val finished = p.waitFor(30, TimeUnit.SECONDS)
        if (!finished) {
            p.destroyForcibly()
            LauncherLog.log("Exec probe: timed out")
            return false
        }
        val ok = p.exitValue() == 0 && out.contains(token)
        LauncherLog.log(
            if (ok) "Exec probe: this device can run fetched binaries"
            else "Exec probe: exec produced ${p.exitValue()} / ${out.trim().take(200)}",
        )
        return ok
    }
}
