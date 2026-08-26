// MonoRuntime — the .NET the launcher runs its tools on.
//
// Three programs here are .NET applications: il2cpp.dll, Roslyn's csc.dll,
// and the bundle surgery tool. This is what runs them.
//
// It replaces DotnetFetcher, which built a runtime on the device at first
// launch out of Microsoft's linux-arm64 tarball and five packages from
// Debian's pool -- glibc, libstdc++, libssl and friends -- and invoked the
// whole thing through glibc's loader because Android has no
// /lib/ld-linux-aarch64.so.1 to name as an interpreter. That worked, then
// stopped: Android builds each app's seccomp filter from the syscalls bionic
// itself makes and answers everything else with SIGSYS rather than ENOSYS,
// and glibc 2.35 registers restartable sequences during startup. There is no
// fallback from a trap, so the process died before printing a word, and the
// only evidence was exit 159. A newer glibc will not fix it and an older one
// only postpones it: the filter is derived from bionic, so a foreign C
// library is always one syscall away from the same end.
//
// The runtime that has no such problem is the one Microsoft builds for
// Android: .NET's Mono flavour for android-arm64, linked against bionic,
// the same runtime MAUI ships. It is fetched at build time and shipped
// inside the APK, so there is nothing to download, nothing to verify at run
// time, and setup no longer needs the network at all.
//
// Two pieces, in the two places Android allows them:
//
//   lib/arm64-v8a/ holds libmonosgen-2.0.so, its components, and monohost --
//   an ordinary arm64 executable that the installer extracts to a directory
//   it will execute from. Nothing here depends on the app targeting API 28;
//   that requirement belongs to the fetched clang, which is written into the
//   data directory and governed by the SELinux domain targetSdk selects.
//
//   assets/mono-bcl holds the class library, which is data. Assets are not
//   executable and do not need to be -- the assemblies are read and JIT'd,
//   never exec'd -- but they also cannot be opened as files while they are
//   inside the APK, so they are unpacked once into filesDir.

package dev.silksong.launcher

import android.app.ActivityManager
import android.content.Context
import android.os.Bundle
import android.os.RemoteException
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.channelFlow
import kotlinx.coroutines.flow.flowOn
import kotlinx.coroutines.withContext
import java.io.File
import java.io.IOException
import kotlin.coroutines.coroutineContext

object MonoRuntime {

    /** Where the class library is unpacked to. */
    private const val BCL = "mono-bcl"

    /** Bumped when the staged copy has to be replaced rather than reused. */
    private const val STAGE_VERSION = "1"

    data class Progress(val step: String, val fraction: Float, val detail: String = "")

    /**
     * The host, as the installer left it.
     *
     * Not an executable any more, and not exec'd: see MonoProvider for why the
     * runtime needs an app process rather than a child process.
     */
    fun runtimeDir(context: Context): File = File(context.applicationInfo.nativeLibraryDir)

    fun bclDir(context: Context): File = File(context.filesDir, BCL)

    private fun stamp(context: Context) = File(bclDir(context), ".staged")

    /**
     * True when the runtime is ready to be used.
     *
     * The native side is part of the APK, so its absence is not a state that
     * can be repaired here -- it means the APK was assembled without it, and
     * saying so plainly beats failing later inside a converter.
     */
    fun isPresent(context: Context): Boolean =
        File(runtimeDir(context), "libmonojni.so").isFile && stamp(context).isFile

    fun environment(context: Context): Map<String, String> = mapOf(
        // il2cpp's runtimeconfig asks for invariant globalization and the
        // runtime carries no ICU for Android; monojni sets the same thing as
        // a runtime property, and this covers anything that reads the
        // environment instead.
        "DOTNET_SYSTEM_GLOBALIZATION_INVARIANT" to "1",
        "HOME" to context.filesDir.absolutePath,
        "TMPDIR" to File(context.filesDir, "tmp").apply { mkdirs() }.absolutePath,
    )

    /**
     * Runs a .NET program in :builder and collects what it printed.
     *
     * Deliberately the same shape as Toolchain.exec, and returning the same
     * Result, because until the runtime moved into a process of its own that
     * is exactly what this was -- an argv and a subprocess. Callers pass an
     * assembly and its arguments instead of a command line, and are otherwise
     * unchanged.
     *
     * The output file is the whole protocol. :builder redirects stdout and
     * stderr onto it and writes the result file when the run ends, so this
     * tails the one until the other appears. A pipe would say "the run is
     * over" by reaching end of file, which is tidier, but the descriptor
     * cannot be handed across: the activity manager refuses an Intent
     * carrying one, and there is no Intent here anyway. Both processes are
     * the same application, so a path in the cache directory is a channel
     * they already share.
     */
    suspend fun exec(
        context: Context,
        assembly: File,
        args: List<String> = emptyList(),
        cwd: File? = null,
        env: Map<String, String> = emptyMap(),
        onLine: (String) -> Unit = {},
    ): Toolchain.Result = withContext(Dispatchers.IO) {
        if (!assembly.isFile) throw IOException("no such assembly: $assembly")

        val stamp = System.nanoTime()
        val out = File(context.cacheDir, "mono-$stamp.out")
        val result = File(context.cacheDir, "mono-$stamp.exit")
        out.delete(); result.delete()
        out.createNewFile()

        val merged = environment(context) + env
        val flatEnv = ArrayList<String>(merged.size * 2)
        for ((k, v) in merged) { flatEnv += k; flatEnv += v }

        val request = Bundle().apply {
            putString(MonoProvider.KEY_ASSEMBLY, assembly.absolutePath)
            putStringArray(MonoProvider.KEY_ARGS, args.toTypedArray())
            putString(MonoProvider.KEY_CWD, cwd?.absolutePath)
            putString(MonoProvider.KEY_BCL, bclDir(context).absolutePath)
            putString(MonoProvider.KEY_RUNTIME, runtimeDir(context).absolutePath)
            putStringArray(MonoProvider.KEY_ENV, flatEnv.toTypedArray())
            putString(MonoProvider.KEY_OUT, out.absolutePath)
            putString(MonoProvider.KEY_RESULT, result.absolutePath)
        }

        val collected = StringBuilder()
        var died = false
        var exitCode: Int? = null
        // Stops :builder when the caller goes away. Toolchain.exec did the
        // same with destroyForcibly, and for the same reason: without it a
        // cancelled build leaves il2cpp running on every core with several
        // gigabytes held. Killing the process is the only lever there is --
        // the run is a thread in another process, and nothing about a
        // provider offers to interrupt it.
        //
        // Left registered for the life of the job rather than disposed on the
        // way out. Disposing in the finally below looks tidy and cannot work:
        // that block runs as cancellation unwinds, which is before this job
        // completes, so the handler was always removed a moment before the
        // only event that would have fired it.
        //
        // Cancellation only. Any other ending leaves the process alone,
        // because killBuilder is package-wide and takes every process of this
        // app that is not in the foreground -- :launcher among them, which is
        // where the build is being run from. A step that failed is not a
        // reason to end a build the user is still waiting on; a cancelled one
        // is already over.
        coroutineContext[Job]?.invokeOnCompletion { cause ->
            if (cause is CancellationException) runCatching { killBuilder(context) }
        }
        try {
            startRun(context, request)

            // Tail. Reading stops one pass *after* the result file appears,
            // not as soon as it does: the last of the output may still have
            // been in flight when the run ended, and a build log that loses
            // its final line is a build log that loses the error.
            var offset = 0L
            val carry = StringBuilder()
            var pending = ByteArray(0)
            var done = false
            var idle = 0
            val started = System.currentTimeMillis()
            while (true) {
                val finished = result.isFile
                val len = out.length()
                if (len > offset) {
                    java.io.RandomAccessFile(out, "r").use { raf ->
                        raf.seek(offset)
                        val buf = ByteArray((len - offset).coerceAtMost(1 shl 20).toInt())
                        val got = raf.read(buf)
                        if (got > 0) {
                            offset += got
                            // A read can stop anywhere, including halfway
                            // through a UTF-8 sequence. Whatever does not
                            // decode is carried to the next read rather than
                            // turned into replacement characters.
                            val merged = pending + buf.copyOf(got)
                            val cut = wholeChars(merged)
                            carry.append(String(merged, 0, cut, Charsets.UTF_8))
                            pending = merged.copyOfRange(cut, merged.size)
                        }
                    }
                    var nl = carry.indexOf("\n")
                    while (nl >= 0) {
                        val line = carry.substring(0, nl)
                        carry.delete(0, nl + 1)
                        if (collected.length < MAX_CAPTURED) collected.append(line).append('\n')
                        onLine(line)
                        nl = carry.indexOf("\n")
                    }
                    idle = 0
                    continue
                }
                if (done) break
                if (finished) { done = true; continue }

                // Is :builder still there?
                //
                // The result file is written by the native side, from an
                // atexit handler that survives Environment.Exit -- but not
                // everything that can end a process gives it the chance. A
                // SIGSEGV, or the low memory killer deciding that a process
                // holding several gigabytes is the one to take, ends it with
                // nothing written. Then no more output ever arrives and the
                // result file never appears, and without this the loop waits
                // for one or the other until the app is killed too.
                //
                // Deliberately not "was it ever seen alive": the process can
                // die before the first poll -- a missing crypto class aborts
                // it in well under a second -- and a rule that only fires
                // after a sighting would wait for ever for one that never
                // came. A grace period is enough, because the only thing
                // being distinguished is "not started yet" from "gone".
                if (++idle >= 8) {
                    idle = 0
                    if (System.currentTimeMillis() - started > STARTUP_GRACE_MS &&
                        !builderAlive(context) && !result.isFile
                    ) { died = true; break }
                }
                delay(120)
            }
            if (carry.isNotEmpty()) {
                if (collected.length < MAX_CAPTURED) collected.append(carry)
                onLine(carry.toString())
            }
        } finally {
            out.delete()
            // Read before this, so deleting here rather than after the read
            // is what keeps a cancelled run from leaving one behind.
            val code = result.takeIf { it.isFile }?.readText()?.trim()?.toIntOrNull()
            result.delete()
            exitCode = code
        }

        if (died) {
            val note = "the build process was killed before it finished; " +
                "on a large conversion this is usually the system reclaiming its memory"
            LauncherLog.log("mono: $note")
            onLine("monojni: $note")
            if (collected.length < MAX_CAPTURED) collected.append(note).append('\n')
        }

        Toolchain.Result(exitCode ?: MonoProvider.FAILED, collected.toString())
    }

    /** As much output as is kept; the rest is streamed to [onLine] and dropped. */
    private const val MAX_CAPTURED = 1 shl 20

    /**
     * Starts the run in :builder, which is the part that can be refused.
     *
     * A binder call into the provider is what creates that process.
     * Deliberately not startService: an app in the background is not allowed
     * to start one, and a build outlives the user's attention. But acquiring a
     * provider is not the certainty it looks like -- ContentResolver.call
     * throws "Unknown authority" for every reason the activity manager has to
     * hand back nothing, and a build asks for this twenty times over tens of
     * minutes with a process death between each.
     *
     * Two of those reasons are ours to avoid, and both come from the same
     * fact: a run ends by killing its own process, and the activity manager
     * only learns of it when binder reports the death. Until it does, the
     * record for the provider still names a process that is gone, and a call
     * arriving in that window is answered with nothing rather than with a new
     * process. So the previous process is waited out first, and a refusal is
     * retried rather than ending the build -- one lost race is not a reason to
     * throw away an hour of conversion. See issue #4, where the third of three
     * runs was made in the same second as the second one's death.
     *
     * The client is deliberately the unstable kind. The platform kills
     * processes that hold a *stable* reference to a provider whose process
     * dies, which is precisely what this one does after every run; unstable
     * says "expected", leaves the launcher alone, and lets the activity
     * manager drop the connection itself.
     */
    private suspend fun startRun(context: Context, request: Bundle) {
        var refusal = "no reason given"
        for (attempt in 1..START_ATTEMPTS) {
            awaitBuilderGone(context)
            val client = runCatching {
                context.contentResolver.acquireUnstableContentProviderClient(MonoProvider.uri(context))
            }.getOrNull()
            if (client != null) {
                try {
                    client.call(MonoProvider.METHOD_RUN, null, request)
                    return
                } catch (e: RemoteException) {
                    // The process was there and went away between acquiring it
                    // and asking it to run.
                    refusal = e.toString()
                } finally {
                    runCatching { client.close() }
                }
            } else {
                refusal = "the activity manager would not start it"
            }
            LauncherLog.log("mono: $BUILDER_PROCESS did not start, attempt $attempt of $START_ATTEMPTS ($refusal)")
            if (attempt < START_ATTEMPTS) delay(START_RETRY_MS * attempt)
        }
        throw IOException(
            "the build process could not be started: $refusal. " +
                "Close the app from the recents screen and start the build again -- " +
                "it carries on from where it stopped.",
        )
    }

    /**
     * Waits for the previous run's process to actually be gone.
     *
     * One process hosts one run, and the next one cannot be started until the
     * last has been reaped: see [startRun]. Normally this returns at once --
     * the launcher stops reading when the result file appears, which is a
     * moment before the process ends, so there is a fraction of a second to
     * wait for and no more.
     *
     * A process that outstays that is not this run's and is not going to
     * become it. Leaving it would fail differently and worse: the provider
     * would be acquired, the run refused by the process already using it, and
     * the build would blame the compiler.
     *
     * So it is asked to quit, and asking always reaches it: it is here
     * because it is running, and a running process is one the provider can be
     * acquired from. Deliberately not [killBuilder], which is
     * killBackgroundProcesses -- that is package-wide and takes every process
     * of this app that is not in the foreground, including :launcher, which
     * is where the build is being run from. A build the user has pressed Home
     * on is precisely the case MonoProvider exists to survive, so the one
     * thing this must not do is end it.
     *
     * It stays as the last resort for a process that will not go on its own,
     * and only while :launcher is in the foreground and therefore not among
     * what it would take. Otherwise the straggler is left alone and the run
     * is attempted anyway: it will fail, but a failed run is recoverable and
     * a killed launcher is not.
     *
     * A device that will not say which of its own processes exist gets none
     * of this and goes straight to the call -- [builderRunning] answering
     * nothing is treated as "not there", so nothing is waited for and no
     * straggler is ever noticed. That is the right way round: the retries in
     * [startRun] still cover the race this exists for, whereas waiting on a
     * question that has no answer would cost every run its full timeout.
     */
    private suspend fun awaitBuilderGone(context: Context) {
        if (waitBuilderGone(context)) return
        LauncherLog.log("mono: $BUILDER_PROCESS is still up from an earlier run; asking it to quit")
        quitBuilder(context)
        if (waitBuilderGone(context)) return
        if (!launcherForeground()) {
            LauncherLog.log("mono: $BUILDER_PROCESS will not quit, and :launcher is in the background; leaving it")
            return
        }
        LauncherLog.log("mono: $BUILDER_PROCESS will not quit; ending this app's background processes")
        killBuilder(context)
        waitBuilderGone(context)
    }

    /**
     * Asks the :builder process to end itself.
     *
     * The reply is not interesting and neither is a failure to get one: a
     * dead provider is a dead process, which is what was being asked for.
     * Whether it worked is settled by looking, not by this.
     */
    private fun quitBuilder(context: Context) {
        val client = runCatching {
            context.contentResolver.acquireUnstableContentProviderClient(MonoProvider.uri(context))
        }.getOrNull() ?: return
        try {
            runCatching { client.call(MonoProvider.METHOD_QUIT, null, null) }
        } finally {
            runCatching { client.close() }
        }
    }

    /**
     * Whether this process is one the user is looking at.
     *
     * Which is the same question as whether killBackgroundProcesses would
     * take it: that reaps everything at or below service importance, and a
     * launcher with no foreground service is a cached process the moment the
     * user leaves. Not being able to tell counts as "no" -- the cost of
     * being wrong one way is a straggler, and the other way is the build.
     */
    private fun launcherForeground(): Boolean = runCatching {
        val state = ActivityManager.RunningAppProcessInfo()
        ActivityManager.getMyMemoryState(state)
        state.importance <= ActivityManager.RunningAppProcessInfo.IMPORTANCE_FOREGROUND
    }.getOrDefault(false)

    /** True if the process is gone before [BUILDER_EXIT_WAIT_MS] is up. */
    private suspend fun waitBuilderGone(context: Context): Boolean {
        val deadline = System.currentTimeMillis() + BUILDER_EXIT_WAIT_MS
        while (builderRunning(context) == true) {
            if (System.currentTimeMillis() >= deadline) return false
            delay(50)
        }
        return true
    }

    /**
     * Whether the :builder process exists, or null when that cannot be told.
     *
     * getRunningAppProcesses has been useless for looking at other apps since
     * Android 5, and that is not what this is for: an app can still see its
     * own processes, which is exactly the question here. It can still answer
     * nothing at all, though, and the two callers want opposite things from
     * that -- so the uncertainty is returned rather than resolved here.
     */
    private fun builderRunning(context: Context): Boolean? {
        val am = context.getSystemService(Context.ACTIVITY_SERVICE) as? ActivityManager ?: return null
        return runCatching {
            am.runningAppProcesses?.any { it.processName.endsWith(BUILDER_PROCESS) }
        }.getOrNull()
    }

    /**
     * True while the :builder process exists.
     *
     * Unknown counts as alive: this decides whether a run that has gone quiet
     * is dead, and a device that will not answer the question is no reason to
     * declare it.
     */
    private fun builderAlive(context: Context): Boolean = builderRunning(context) ?: true

    /**
     * Ends every process of this app that is not in the foreground, :builder
     * included.
     *
     * The bluntest thing here, and never the first thing tried. On cancel it
     * is the only thing that will do: the run is a thread in another process
     * that nothing here can interrupt, and a cancelled build must not leave
     * il2cpp holding several gigabytes on every core. :launcher going with it
     * costs nothing there, because the build is already over.
     *
     * [awaitBuilderGone] uses it too, but only after asking nicely and only
     * while :launcher is in the foreground and so out of its reach. Anywhere
     * else it would be a bug -- a build the user has pressed Home on is a
     * background process like any other.
     *
     * Needs KILL_BACKGROUND_PROCESSES, which is a normal permission: granted
     * at install, no prompt. If it is somehow refused, the worst case is the
     * process outliving a cancelled build, which is where this started.
     */
    private fun killBuilder(context: Context) {
        val am = context.getSystemService(Context.ACTIVITY_SERVICE) as? ActivityManager ?: return
        runCatching { am.killBackgroundProcesses(context.packageName) }
    }

    /** Must match android:process on MonoProvider in the manifest. */
    private const val BUILDER_PROCESS = ":builder"

    /**
     * How long :builder is allowed to not exist before it counts as dead.
     *
     * Only has to cover the gap between the run being asked for and the
     * process appearing, which is a process fork; a second is generous.
     */
    private const val STARTUP_GRACE_MS = 4_000L

    /**
     * How many times a refused start is asked for again.
     *
     * The thing being waited out is a binder death reaching the activity
     * manager, which is tens of milliseconds; the rest is for a device with
     * nothing to spare, since a build is the busiest this phone has been all
     * week. Four attempts spread over three seconds is nothing against a
     * conversion that takes half an hour, and a fifth would not tell us
     * anything a fourth did not: past this, the refusal is not a race and
     * only the app being restarted will clear it.
     */
    private const val START_ATTEMPTS = 4
    private const val START_RETRY_MS = 500L

    /**
     * How long the previous run's process is given to disappear.
     *
     * Killing itself is the last thing it does and it is a signal, so this is
     * normally over in a fraction of a second. The bound is here for the
     * process that will not go, and it is what that costs before it is killed
     * -- once per run, so it is deliberately short.
     */
    private const val BUILDER_EXIT_WAIT_MS = 3_000L

    /**
     * How much of [b] decodes as whole UTF-8 characters.
     *
     * Returns the length to decode now, leaving any truncated sequence at the
     * end for the next read to complete. Only the last three bytes can be
     * part of one, so this looks no further back than that.
     */
    private fun wholeChars(b: ByteArray): Int {
        var i = b.size - 1
        val floor = maxOf(0, b.size - 3)
        while (i >= floor) {
            val c = b[i].toInt() and 0xFF
            // A continuation byte is 10xxxxxx; anything else starts a
            // character, and its length says whether it is all here.
            if (c and 0xC0 != 0x80) {
                val need = when {
                    c and 0x80 == 0x00 -> 1
                    c and 0xE0 == 0xC0 -> 2
                    c and 0xF0 == 0xE0 -> 3
                    c and 0xF8 == 0xF0 -> 4
                    else -> 1   // not a lead byte at all; let the decoder have it
                }
                return if (i + need <= b.size) b.size else i
            }
            i--
        }
        return b.size
    }

    /**
     * Unpacks the class library out of the APK.
     *
     * Assets cannot be opened as files while they are in the APK -- they are
     * entries in a zip, and the runtime wants paths -- so they are copied
     * once. The stamp is written last and names the version it was written
     * for, so an interrupted copy is not mistaken for a finished one and an
     * upgrade replaces what the previous version left.
     */
    fun stage(context: Context): Flow<Progress> = channelFlow {
        val dir = bclDir(context)
        val mark = stamp(context)
        if (mark.isFile && mark.readText().trim() == STAGE_VERSION) {
            send(Progress(".NET ready", 1f))
            return@channelFlow
        }

        if (!File(runtimeDir(context), "libmonojni.so").isFile) {
            throw IOException(
                "the .NET host is missing from this build: " +
                    "${File(runtimeDir(context), "libmonojni.so")}. " +
                    "The APK was assembled without it.",
            )
        }

        dir.deleteRecursively()
        dir.mkdirs()

        val names = context.assets.list(BCL)?.toList().orEmpty()
        if (names.isEmpty()) throw IOException("the APK carries no $BCL assets")

        send(Progress("Unpacking .NET", 0f, "${names.size} files"))
        for ((n, name) in names.withIndex()) {
            context.assets.open("$BCL/$name").use { input ->
                File(dir, name).outputStream().use { input.copyTo(it) }
            }
            send(Progress("Unpacking .NET", (n + 1f) / names.size, name))
        }

        if (!File(dir, "System.Private.CoreLib.dll").isFile) {
            throw IOException("the class library is incomplete: System.Private.CoreLib.dll is missing")
        }

        mark.writeText(STAGE_VERSION)
        LauncherLog.log("staged the .NET class library: ${names.size} files")
        send(Progress(".NET ready", 1f))
    }.flowOn(Dispatchers.IO)
}
