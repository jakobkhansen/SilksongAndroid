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
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.content.ServiceConnection
import android.os.Build
import android.os.Bundle
import android.os.IBinder
import android.os.Message
import android.os.Messenger
import android.os.RemoteException
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.channelFlow
import kotlinx.coroutines.flow.flowOn
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeoutOrNull
import java.io.File
import java.io.IOException
import java.util.concurrent.atomic.AtomicInteger

object MonoRuntime {

    /** Where the class library is unpacked to. */
    private const val BCL = "mono-bcl"

    /** Bumped when the staged copy has to be replaced rather than reused. */
    private const val STAGE_VERSION = "1"

    data class Progress(val step: String, val fraction: Float, val detail: String = "")

    /**
     * The host, as the installer left it.
     *
     * Not an executable any more, and not exec'd: see MonoService for why the
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
    ) + budget(context).toEnv()

    /**
     * How much of the device one .NET run is allowed to help itself to.
     *
     * The conversion is the largest thing this app ever asks a machine to do,
     * and on a phone the machine is shared with everything else the user has
     * open. Android does not refuse the memory; it takes the process instead,
     * which arrives as "the system stopped the build process to reclaim
     * memory" and forty seconds of wasted work. A 4 GB device reported 1.9 GB
     * free at the start of a conversion and lost the process before the first
     * hundred files.
     *
     * Two numbers, because il2cpp's appetite has two sources. It converts
     * assemblies in parallel, and every worker holds its own share of the type
     * graph, so peak memory rises with the number of workers; and it allocates
     * hard enough that SGen's default policy -- let the heap roughly double
     * before collecting again -- floats far more garbage than a small device
     * can carry.
     *
     * [heapMb] of zero means no limit at all, which is what a device with room
     * to spare gets: the phones this already works on should keep the runtime
     * they were measured with, and a collector tuned for a 4 GB device is
     * slower on one that never needed it.
     */
    data class Budget(val cores: Int, val heapMb: Int) {

        /**
         * The environment that imposes it.
         *
         * DOTNET_PROCESSOR_COUNT is the whole of the first half. Under this
         * runtime Environment.ProcessorCount is an icall to mono_cpu_limit(),
         * which reads that variable before it looks at the machine, so every
         * degree of parallelism the program derives from the core count --
         * Parallel.For, the thread pool's own floor, the collector's
         * workers -- follows from it. That matters more than any il2cpp option
         * would: it needs no cooperation from the tool, and it cannot be
         * spelled wrong in a way that goes unnoticed, because the runtime
         * either reads it or the count is unchanged.
         *
         * MONO_GC_PARAMS is the second half, and is read once when the
         * collector starts -- which is why [MonoService] gets these through
         * the environment rather than as host properties: monojni sets them
         * with setenv before it loads the runtime at all.
         *
         *   soft-heap-limit  past this size the collector stops letting the
         *                    heap grow by a proportion of itself and allows
         *                    only a nursery's worth of growth between major
         *                    collections, forcing each one rather than letting
         *                    it run concurrently. This is the knob that trades
         *                    time for memory, and the only one that does so
         *                    without an upper bound to be wrong about: an
         *                    unset max-heap-size stays unlimited, so a heap
         *                    that genuinely needs to be larger than this still
         *                    grows rather than throwing.
         *   nursery-size     pinned, which also turns off the dynamic nursery
         *                    that would otherwise grow -- and, past the soft
         *                    limit, that growth allowance is measured in
         *                    nurseries, so pinning it small keeps it small.
         *   major=marksweep  the serial collector rather than the concurrent
         *                    one. Concurrent marking keeps a worker and its
         *                    working set alive and lets the program allocate
         *                    throughout the mark; neither is worth having when
         *                    the problem is memory and the cores are already
         *                    capped.
         *
         * Unknown or malformed options are reported and ignored by SGen rather
         * than being fatal, so the worst a mistake here costs is the tuning.
         */
        fun toEnv(): Map<String, String> = if (heapMb <= 0) emptyMap() else mapOf(
            "DOTNET_PROCESSOR_COUNT" to cores.toString(),
            "MONO_GC_PARAMS" to "major=marksweep,nursery-size=4m,soft-heap-limit=${heapMb}m",
        )

        /**
         * The next step down, for a run that has already been reclaimed once.
         *
         * Halving both rather than picking a new tier: what was tried is known
         * to be too much for this device as it stood, and how much too much is
         * not knowable from here. Null at the floor, which is the point at
         * which there is nothing left to give up and the failure is the
         * device's rather than the settings'.
         */
        fun tighter(): Budget? = when {
            heapMb <= 0 -> Budget(cores.coerceAtMost(4), 1024)
            cores <= 1 && heapMb <= FLOOR_HEAP_MB -> null
            else -> Budget((cores / 2).coerceAtLeast(1), (heapMb / 2).coerceAtLeast(FLOOR_HEAP_MB))
        }

        override fun toString(): String =
            if (heapMb <= 0) "$cores cores, heap unlimited" else "$cores cores, $heapMb MB heap"
    }

    /** Below this there is no point continuing to squeeze; il2cpp needs it. */
    private const val FLOOR_HEAP_MB = 384

    /**
     * What this device gets, from what it has rather than what is free.
     *
     * Total memory rather than available: available is whatever the user
     * happened to have open when the button was pressed, and a build that
     * silently converts with two workers because a browser was in the
     * background is a build whose duration nobody can explain. Total is a
     * property of the phone, so the same phone always builds the same way.
     *
     * The tiers are deliberately coarse and the top one is "change nothing":
     * every device this is known to work on is above it, and they should keep
     * the behaviour they were measured with.
     *
     * The boundaries sit well inside each band rather than on it, because
     * totalMem is not the number on the box. The kernel's own carveout comes
     * off it first, and how much varies by a few percent between devices: a
     * Retroid Pocket Flip 2 with 8 GB reports 7.52, and 4 GB devices report
     * anywhere from about 3.4 to 3.9. A threshold placed at the nominal figure
     * therefore splits a single class of device in two, and the half that
     * falls through lands a whole tier below where it belongs -- a 4 GB phone
     * would convert on one core because it happened to report 3.4 rather than
     * 3.6. Each cut is made in the empty space between bands instead.
     */
    fun budget(context: Context): Budget {
        val cores = Runtime.getRuntime().availableProcessors().coerceAtLeast(1)
        val total = runCatching {
            val am = context.getSystemService(Context.ACTIVITY_SERVICE) as ActivityManager
            val mi = ActivityManager.MemoryInfo()
            am.getMemoryInfo(mi)
            mi.totalMem
        }.getOrDefault(0L)
        val gb = total / 1024.0 / 1024.0 / 1024.0
        return when {
            // Unknown is treated as roomy: a device that will not answer the
            // question is not evidence that it is small, and capping every
            // such device would slow builds that were never in trouble.
            total <= 0L || gb >= 7.0 -> Budget(cores, 0)
            gb >= 5.2 -> Budget(cores.coerceAtMost(4), 1024)
            gb >= 3.2 -> Budget(cores.coerceAtMost(2), 640)
            else -> Budget(1, FLOOR_HEAP_MB)
        }
    }

    /**
     * Runs a .NET program in a builder process and collects what it printed.
     *
     * Deliberately the same shape as Toolchain.exec, and returning the same
     * Result, because until the runtime moved into a process of its own that
     * is exactly what this was -- an argv and a subprocess. Callers pass an
     * assembly and its arguments instead of a command line, and are otherwise
     * unchanged.
     *
     * The output file is the whole protocol. The builder process redirects
     * stdout and stderr onto it and writes the result file when the run ends,
     * so this tails the one until the other appears. A pipe would say "the run
     * is over" by reaching end of file, which is tidier, and it would have to
     * survive a process whose last act is to kill itself. Both processes are
     * the same application, so a path in the cache directory is a channel they
     * already share, and it keeps its contents after the writer has gone.
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

        // Wall clock, for matching against the platform's record of process
        // deaths later: those are timestamped, and a record from an earlier
        // run would be the wrong answer confidently given.
        val startedWall = System.currentTimeMillis()

        val stamp = System.nanoTime()
        val out = File(context.cacheDir, "mono-$stamp.out")
        val result = File(context.cacheDir, "mono-$stamp.exit")
        out.delete(); result.delete()
        out.createNewFile()

        val merged = environment(context) + env
        val flatEnv = ArrayList<String>(merged.size * 2)
        for ((k, v) in merged) { flatEnv += k; flatEnv += v }

        val request = Bundle().apply {
            putString(MonoService.KEY_ASSEMBLY, assembly.absolutePath)
            putStringArray(MonoService.KEY_ARGS, args.toTypedArray())
            putString(MonoService.KEY_CWD, cwd?.absolutePath)
            putString(MonoService.KEY_BCL, bclDir(context).absolutePath)
            putString(MonoService.KEY_RUNTIME, runtimeDir(context).absolutePath)
            putStringArray(MonoService.KEY_ENV, flatEnv.toTypedArray())
            putString(MonoService.KEY_OUT, out.absolutePath)
            putString(MonoService.KEY_RESULT, result.absolutePath)
        }

        val collected = StringBuilder()
        var died = false
        var lowMemory = false
        var exitCode: Int? = null
        var peakKb = 0L
        var sampled = 0L
        // Held for as long as the run, and released in the finally below. See
        // startRun: this binding is what keeps the builder process off the
        // bottom of the activity manager's list while it works.
        var binding: Binding? = null
        // Which of the two processes the run ended up in, for the questions
        // below that have to name it.
        var slot: Slot? = null
        // And which process, for the same reason: a builder is identified by
        // pid, not by name. See Binding.pid.
        var pid = 0
        // Whether the tail below got to the end of the run on its own. False
        // means something took this coroutine away mid-run -- a cancelled
        // build, most often -- and the process on the other end is still
        // going. See the finally.
        var ended = false
        try {
            val builder = startRun(context, request)
            binding = builder
            slot = builder.slot
            pid = builder.pid

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

                // Is the builder process still there?
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
                        !builderAlive(context, builder.slot, builder.pid) && !result.isFile
                    ) { died = true; break }
                }
                // What the run is actually costing, sampled while it is still
                // there to ask. Every estimate of what a conversion needs has
                // so far come from one measurement on one phone, extrapolated
                // to devices nobody here owns -- and it has been wrong twice.
                // A number taken on the machine that struggled is worth more
                // than any amount of reasoning about it, and it is only
                // useful if it was recorded before the process went away.
                val now = System.currentTimeMillis()
                if (now - sampled >= RSS_SAMPLE_MS) {
                    sampled = now
                    val rss = builderRssKb(builder.pid)
                    if (rss > peakKb) peakKb = rss
                }
                delay(120)
            }
            if (carry.isNotEmpty()) {
                if (collected.length < MAX_CAPTURED) collected.append(carry)
                onLine(carry.toString())
            }
            ended = true
        } finally {
            // First: it is the run's claim on the process, and the run is over
            // one way or another.
            //
            // A run that ended on its own has already killed its process, or
            // is a moment from doing so, and is left to finish: unbinding is
            // all that is owed. A run that did not is still going, and leaving
            // it would leave il2cpp on every core with several gigabytes held
            // for as long as the app lives -- so the process is ended, which
            // is the only lever there is. The run is a thread in another
            // process and nothing about a binding offers to interrupt it.
            //
            // Cancellation is the case this is for and not the only one: an
            // exception from anywhere in the tail -- onLine writing to a log
            // that has gone away, say -- orphans the run just as thoroughly.
            //
            // Down the binding rather than through killBackgroundProcesses,
            // which is what this used to be. That is package-wide and takes
            // every process of this app that is not in the foreground --
            // :launcher among them, which is where the build is being run
            // from, and which is in the background in exactly the case worth
            // surviving.
            runCatching { if (ended) binding?.close() else binding?.quit() }
            out.delete()
            // Read before this, so deleting here rather than after the read
            // is what keeps a cancelled run from leaving one behind.
            val code = result.takeIf { it.isFile }?.readText()?.trim()?.toIntOrNull()
            result.delete()
            exitCode = code
        }

        // What the program printed, when fd 1 did not have it.
        //
        // The redirect above catches everything the NATIVE side writes, and
        // nothing a managed program does: .NET on Android sends Console to the
        // Android log under the tag "DOTNET" rather than to standard output,
        // so a compiler that fails with a screen of diagnostics leaves the
        // capture file empty and the launcher reports "0 errors". The log is
        // the only place those lines exist, and a process may read its own.
        //
        // Only on the way out of a run that failed: a run that succeeded has
        // nothing worth the round trip, and the cost lands on the path that is
        // already about to throw.
        if (collected.isEmpty() && (exitCode ?: MonoService.FAILED) != 0 && pid != 0) {
            runCatching {
                val logcat = ProcessBuilder("logcat", "-d", "--pid=$pid", "-s", "DOTNET")
                    .redirectErrorStream(true).start()
                logcat.inputStream.bufferedReader().forEachLine { line ->
                    // "I/DOTNET  ( 6557): the line" -- the prefix is logcat's,
                    // not the program's, and callers parse what the program said.
                    val said = line.substringAfter("): ", "").trimEnd()
                    if (said.isNotEmpty() && collected.length < MAX_CAPTURED) {
                        collected.append(said).append('\n')
                        onLine(said)
                    }
                }
                logcat.waitFor()
            }
        }

        if (died) {
            val death = deathOf(context, slot, pid, startedWall)
            lowMemory = death.lowMemory
            LauncherLog.log("mono: ${death.note}")
            onLine("monojni: ${death.note}")
            if (collected.length < MAX_CAPTURED) collected.append(death.note).append('\n')
        }
        // Only for runs big enough to be worth a line. The compilers finish in
        // seconds and take a few hundred megabytes; the conversion is the one
        // that has ever been near the edge, and it is the one this is for.
        if (peakKb >= RSS_LOG_FLOOR_KB) {
            LauncherLog.log("mono: ${assembly.name} peaked at ${gb(peakKb * 1024)}; ${memory(context)}")
        }

        Toolchain.Result(exitCode ?: MonoService.FAILED, collected.toString(), lowMemory)
    }

    /** As much output as is kept; the rest is streamed to [onLine] and dropped. */
    private const val MAX_CAPTURED = 1 shl 20

    /**
     * Starts the run in a builder process, which is the part that can be
     * refused.
     *
     * bindService with BIND_AUTO_CREATE is what creates that process.
     * Deliberately not startService, which an app in the background may not
     * call from API 26 onwards and a build outlives the user's attention; and
     * deliberately not a ContentProvider any more, which had the same reach
     * and no way back from a refusal. See MonoService for both.
     *
     * Three things can go wrong, and all three are survivable here. bindService
     * can return false, which is the activity manager saying it will not even
     * try. The binding can be accepted and never connect, which is the process
     * failing to come up. And the process can come up and go away between
     * connecting and being handed the run. Each is retried rather than ending
     * the build: one lost start is not a reason to throw away an hour of
     * conversion.
     *
     * Every attempt takes the other process, because much the likeliest reason
     * a start is refused is the previous run's process. A run ends by killing
     * itself, and the activity manager will not start a second process with
     * the same name until that death has been reaped -- it holds the new start
     * until it is, and cancels it outright after ten seconds. Alternating
     * means an attempt never queues behind the funeral the last one tripped
     * over. See issue #4, where a device that was slow to reap refused every
     * run from the third onwards and stayed that way across a restart of the
     * app, because the state that refuses lives in system_server.
     *
     * The binding is returned rather than dropped here, and the caller holds
     * it until the run is over. Releasing it at once looks right -- the
     * message is one way, so the run is already going -- and it is what cost a
     * user their conversion: a process nothing is bound to is a cached
     * process, the cheapest thing on the device to reclaim, and this one
     * spends half an hour holding several gigabytes. A bound service's process
     * is raised towards the processes bound to it, so an open binding from the
     * foreground :launcher is the difference between il2cpp being the first
     * thing killed under pressure and it being roughly as safe as the screen
     * the user is watching. See issue #6, where a conversion was taken at
     * 583s.
     */
    private suspend fun startRun(context: Context, request: Bundle): Binding {
        val since = System.currentTimeMillis()
        var refusal = "no reason given"
        for (attempt in 1..START_ATTEMPTS) {
            val slot = nextSlot()
            awaitBuilderGone(context, slot)
            val binding = Binding(context.applicationContext, slot)
            // Every way out of the attempt except the one that hands the
            // binding to the caller has to release it. A registered connection
            // nobody owns holds a builder process alive for as long as the
            // launcher lives, and the wait below is a suspension point, so a
            // build cancelled while a builder is coming up unwinds straight
            // through here.
            var keep = false
            try {
                val messenger = binding.connect(CONNECT_TIMEOUT_MS)
                if (messenger != null) {
                    messenger.send(
                        Message.obtain(null, MonoService.MSG_RUN).apply { data = request },
                    )
                    keep = true
                    return binding
                }
                refusal = binding.failure ?: "no reason given"
            } catch (e: RemoteException) {
                // It was there and went away between connecting and being
                // asked to run. This one is finished with; the next attempt
                // takes the other process.
                refusal = e.toString()
            } finally {
                if (!keep) binding.close()
            }
            LauncherLog.log(
                "mono: ${slot.process} did not start, attempt $attempt of $START_ATTEMPTS ($refusal)",
            )
            if (attempt < START_ATTEMPTS) delay(START_RETRY_MS)
        }
        // Whether a process was ever forked is the one thing that separates
        // "the activity manager would not start it" from "it started and
        // something took it", and it is the first thing anyone reading a bug
        // report about this will want. The platform keeps the answer.
        LauncherLog.log("mono: giving up; ${fateOfBuilder(context, since)}")
        throw IOException(
            "the build process could not be started: $refusal. " +
                "Force stop the app in Android's app settings, or restart the device, " +
                "then start the build again -- it carries on from where it stopped.",
        )
    }

    /**
     * The two interchangeable builder processes.
     *
     * See MonoService for why there are two of them. [process] must match
     * android:process on the matching service in the manifest: it is what the
     * activity manager reports back, and what everything below looks for when
     * it asks whether a particular one is still running.
     */
    private enum class Slot(val process: String, val service: Class<out MonoService>) {
        A(":builder", MonoService::class.java),
        B(":builder2", MonoServiceAlt::class.java),
    }

    private val slots = AtomicInteger(0)

    /** The next process to try, which is never the one just tried. */
    private fun nextSlot(): Slot {
        val all = Slot.values()
        return all[(slots.getAndIncrement() and Int.MAX_VALUE) % all.size]
    }

    /**
     * One binding to one builder process, and the wait for it to come up.
     *
     * A ServiceConnection is called back on the main thread and a build runs
     * on Dispatchers.IO, so the connection is handed over rather than waited
     * for. Bound against the application context: the binding outlives any
     * one screen, and an Activity that is torn down while still bound is
     * where "ServiceConnection leaked" comes from.
     */
    private class Binding(private val context: Context, val slot: Slot) : ServiceConnection {

        private val connected = CompletableDeferred<Messenger?>()

        @Volatile private var messenger: Messenger? = null

        @Volatile private var bound = false

        /**
         * The process this run is in, or 0 when it could not be told.
         *
         * By pid and not by name, because the name is no longer unique in
         * time. A run ends by killing its own process while this binding is
         * still held, which the activity manager reads as a service crashing
         * and answers by scheduling a restart a second later -- "Scheduling
         * restart of crashed service ... for connection", once per run. The
         * restart is normally cancelled by the unbind that follows
         * immediately, but the one path that deliberately waits is the one
         * that decides a run has died, and a replacement wearing the same
         * name would make it wait for ever.
         */
        @Volatile var pid = 0
            private set

        /** Why the process did not come up, once [connect] has said it did not. */
        @Volatile var failure: String? = null
            private set

        override fun onServiceConnected(name: ComponentName?, service: IBinder?) {
            val m = service?.let { Messenger(it) }
            messenger = m
            connected.complete(m)
        }

        // A run ends by killing its own process, so losing the service is the
        // ordinary end of a binding rather than a fault. Whether the run
        // produced anything is settled by the result file, not by this.
        override fun onServiceDisconnected(name: ComponentName?) = Unit

        override fun onNullBinding(name: ComponentName?) {
            connected.complete(null)
        }

        /** Binds, and waits for the process to exist. Null if it does not. */
        suspend fun connect(timeoutMs: Long): Messenger? {
            bound = runCatching {
                context.bindService(
                    Intent(context, slot.service),
                    this,
                    // AUTO_CREATE is what starts the process, and holding the
                    // binding is what keeps it out of the cached bucket --
                    // which is all the provider connection this replaces did,
                    // and all that issue #6 needs. Deliberately not
                    // BIND_IMPORTANT: that would also put a process that pegs
                    // every core for ten minutes in the foreground scheduling
                    // group, which is a change to how every device runs a
                    // build and is not what was wrong with this one.
                    //
                    // ABOVE_CLIENT says the builder matters more than the app
                    // that asked for it, which is simply true: :launcher is a
                    // progress bar and can be rebuilt from nothing, while the
                    // builder is holding an hour of work that has to start
                    // again if it goes. Without it the activity manager floors
                    // the builder at the visible bucket even while the
                    // launcher is in the foreground, so on a device short of
                    // memory the process carrying the whole build is offered
                    // to the low memory killer several levels before the one
                    // drawing the screen. It changes only that ordering; the
                    // scheduling group is BIND_IMPORTANT's doing, not this.
                    Context.BIND_AUTO_CREATE or Context.BIND_ABOVE_CLIENT,
                )
            }.getOrDefault(false)
            if (!bound) {
                // bindService can leave the connection registered even when it
                // says no, so it is released either way; unregistering one that
                // was never registered is what the catch is for.
                runCatching { context.unbindService(this) }
                failure = "the activity manager would not start it"
                return null
            }
            val m = withTimeoutOrNull(timeoutMs) { connected.await() }
            if (m == null) {
                failure = if (connected.isCompleted) {
                    "the build process gave nothing to talk to"
                } else {
                    "the build process did not come up within ${timeoutMs / 1000}s"
                }
            } else {
                // Safe to ask now and only now: the binder came back, so the
                // process is up, and nothing else of this name can be.
                pid = builderPid(context, slot)
            }
            return m
        }

        /**
         * Ends the process on the other end, and releases this.
         *
         * Reaches exactly one process, which killBackgroundProcesses could
         * not: see the finally in [exec].
         */
        fun quit() {
            messenger?.let { m ->
                runCatching { m.send(Message.obtain(null, MonoService.MSG_QUIT)) }
            }
            close()
        }

        fun close() {
            if (!bound) return
            bound = false
            runCatching { context.unbindService(this) }
        }
    }

    /**
     * Waits for a straggler in the process about to be used.
     *
     * Rare now, and it used to be every run: a process only ever hosts one
     * run, so the next one needed the last one's process to be gone, and the
     * two were consecutive. Alternating the two slots takes that out -- by the
     * time a slot comes round again, a whole run has happened in the other
     * one, which is seconds at least and usually minutes.
     *
     * What is left is a process that outstayed all of that, which is not this
     * run's and is not going to become it. Starting anyway would fail
     * differently and worse: the binding would connect, the run would be
     * refused by the process already using it, and the build would blame the
     * compiler.
     *
     * [killBuilder] is the only lever, and it is a blunt one --
     * killBackgroundProcesses is package-wide and takes every process of this
     * app that is not in the foreground, including :launcher, which is where
     * the build is being run from. So it is used only while :launcher is in
     * the foreground and therefore out of its reach. Otherwise the straggler
     * is left alone and the run is attempted anyway: it will fail, but a
     * failed run is recoverable and a killed launcher is not.
     *
     * A device that will not say which of its own processes exist gets none of
     * this -- [builderRunning] answering nothing is treated as "not there", so
     * nothing is waited for and no straggler is ever noticed. That is the
     * right way round: [startRun] retries anyway, whereas waiting on a
     * question that has no answer would cost every run its full timeout.
     */
    private suspend fun awaitBuilderGone(context: Context, slot: Slot) {
        // The process about to be used has to be gone: one process hosts one
        // run, and a second arriving in it would be refused.
        val targetGone = waitBuilderGone(context, slot)
        // The other one cannot block this start -- that is what having two is
        // for -- but a builder still up as a run begins is a straggler from an
        // earlier one, because runs are strictly sequential. Nothing here
        // needs it gone; it is worth clearing because it is holding whatever
        // the run that made it was holding, and with one process this was
        // noticed for free.
        val strays = Slot.values().filter { it != slot && builderRunning(context, it) == true }
        if (targetGone && strays.isEmpty()) return

        val up = (if (targetGone) strays else strays + slot).joinToString(", ") { it.process }
        if (!launcherForeground()) {
            LauncherLog.log(
                "mono: $up still up from an earlier run, " +
                    "and :launcher is in the background; leaving it",
            )
            return
        }
        LauncherLog.log(
            "mono: $up still up from an earlier run; ending this app's background processes",
        )
        killBuilder(context)
        waitBuilderGone(context, slot)
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
    private suspend fun waitBuilderGone(context: Context, slot: Slot): Boolean {
        val deadline = System.currentTimeMillis() + BUILDER_EXIT_WAIT_MS
        while (builderRunning(context, slot) == true) {
            if (System.currentTimeMillis() >= deadline) return false
            delay(50)
        }
        return true
    }

    /**
     * Whether a given builder process exists, or null when that cannot be told.
     *
     * getRunningAppProcesses has been useless for looking at other apps since
     * Android 5, and that is not what this is for: an app can still see its
     * own processes, which is exactly the question here. It can still answer
     * nothing at all, though, and the two callers want opposite things from
     * that -- so the uncertainty is returned rather than resolved here.
     *
     * Note that it answers about the process the platform still lists, which
     * is not the same as the process the platform has finished reaping. That
     * gap is the whole reason [startRun] alternates rather than waiting.
     */
    private fun builderRunning(context: Context, slot: Slot): Boolean? {
        val am = context.getSystemService(Context.ACTIVITY_SERVICE) as? ActivityManager ?: return null
        return runCatching {
            am.runningAppProcesses?.any { it.processName.endsWith(slot.process) }
        }.getOrNull()
    }

    /** The pid of a builder process, or 0 when it cannot be told. */
    private fun builderPid(context: Context, slot: Slot): Int = runCatching {
        val am = context.getSystemService(Context.ACTIVITY_SERVICE) as? ActivityManager ?: return 0
        am.runningAppProcesses?.firstOrNull { it.processName.endsWith(slot.process) }?.pid ?: 0
    }.getOrDefault(0)

    /**
     * How much memory a builder is holding, in kB, or 0 when it cannot be told.
     *
     * /proc is readable here because the builder is this app's own process and
     * shares its uid; the hidepid the platform applies keeps other apps out,
     * not us. VmRSS rather than PSS: it is one line of a file this process may
     * already open, where getProcessMemoryInfo is a binder call per sample and
     * has been narrowed for third-party callers more than once.
     */
    private fun builderRssKb(pid: Int): Long = runCatching {
        if (pid <= 0) return 0L
        File("/proc/$pid/status").useLines { lines ->
            lines.firstOrNull { it.startsWith("VmRSS:") }
                ?.filter { it.isDigit() }
                ?.toLongOrNull()
                ?: 0L
        }
    }.getOrDefault(0L)

    /** How often the run's memory is sampled, and the size worth reporting. */
    private const val RSS_SAMPLE_MS = 2_000L
    private const val RSS_LOG_FLOOR_KB = 512L * 1024L

    /**
     * True while the process this run was given still exists.
     *
     * The pid rather than the name: see [Binding.pid]. Falling back to the
     * name when there is no pid keeps a device that will not list its own
     * processes behaving as it did.
     *
     * Unknown counts as alive: this decides whether a run that has gone quiet
     * is dead, and a device that will not answer the question is no reason to
     * declare it.
     */
    private fun builderAlive(context: Context, slot: Slot, pid: Int): Boolean {
        if (pid <= 0) return builderRunning(context, slot) ?: true
        val am = context.getSystemService(Context.ACTIVITY_SERVICE) as? ActivityManager ?: return true
        return runCatching {
            am.runningAppProcesses?.any { it.pid == pid }
        }.getOrNull() ?: true
    }

    /** Whether an exit record names a builder process, and which. */
    private fun isBuilder(name: String, slot: Slot?): Boolean =
        if (slot != null) name.endsWith(slot.process)
        else Slot.values().any { name.endsWith(it.process) }

    /**
     * What became of the processes a refused start asked for.
     *
     * The distinction worth having in a bug report is whether one was ever
     * forked at all. No record means the activity manager never started one,
     * and the refusal is its own; a record means a process did start and
     * something took it, and says what. Guessing between those two is what the
     * old message did, and it guessed wrong on the device that provoked all of
     * this.
     */
    private fun fateOfBuilder(context: Context, since: Long): String {
        val free = memory(context)
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.R) return free
        val am = context.getSystemService(Context.ACTIVITY_SERVICE) as? ActivityManager
            ?: return free
        val info = runCatching {
            am.getHistoricalProcessExitReasons(context.packageName, 0, 20)
                .firstOrNull { isBuilder(it.processName, null) && it.timestamp >= since }
        }.getOrNull()
        val what = info?.let { describeExit(it) }
            ?: "the system has no record of a build process having started"
        return "$what; $free"
    }

    /**
     * Why a builder is not there any more, asked of the platform rather than
     * guessed at.
     *
     * This used to say "on a large conversion this is usually the system
     * reclaiming its memory", which is a plausible sentence and was the wrong
     * one: a Retroid Pocket Flip 2 reported it for a run that had in fact
     * crashed with SIGSEGV while still in the foreground, and the message sent
     * the diagnosis after memory for as long as anyone believed it. A guess
     * printed in the same tone as a fact is worse than no message.
     *
     * Android keeps the answer. getHistoricalProcessExitReasons is readable
     * for one's own package from API 30 and distinguishes the cases that
     * matter here -- reclaimed for memory, killed, crashed, stopped
     * responding -- and free memory at the moment of asking says whether the
     * device was up against it either way.
     *
     * Only a record from this run will do, hence [since]: the previous run
     * ended by killing its own process, so there is always an older record
     * saying SIGKILL, and reporting that one would be a confident lie. The
     * record is written when the death is reaped, which is a moment after the
     * process stops answering, so it is worth waiting briefly for.
     *
     * By [pid] where there is one, for the same reason [Binding.pid] exists:
     * the activity manager restarts a bound service whose process died, so a
     * second record under the same name can belong to a process this run
     * never used.
     */
    private suspend fun deathOf(context: Context, slot: Slot?, pid: Int, since: Long): Death {
        val am = context.getSystemService(Context.ACTIVITY_SERVICE) as? ActivityManager
            ?: return Death("the build process ended before it finished", false)
        val free = "; ${memory(context)}"

        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.R) {
            return Death("the build process ended before it finished$free", false)
        }
        repeat(EXIT_REASON_TRIES) { attempt ->
            if (attempt > 0) delay(EXIT_REASON_WAIT_MS)
            val info = runCatching {
                am.getHistoricalProcessExitReasons(context.packageName, 0, 20)
                    .firstOrNull {
                        if (pid > 0) it.pid == pid
                        else isBuilder(it.processName, slot) && it.timestamp >= since
                    }
            }.getOrNull()
            if (info != null) return Death(describeExit(info) + free, isLowMemory(info))
        }
        return Death(
            "the build process ended before it finished, and the system has not said why$free",
            false,
        )
    }

    /** What the platform said about a process that is no longer there. */
    private data class Death(val note: String, val lowMemory: Boolean)

    /**
     * Whether the platform took the process for its memory.
     *
     * Both reasons count. REASON_LOW_MEMORY is the low memory killer proper;
     * EXCESSIVE_RESOURCE_USAGE is what some devices report for the same
     * decision, and a run that ends either way ends for the same reason and
     * wants the same smaller settings on the next attempt.
     */
    private fun isLowMemory(info: android.app.ApplicationExitInfo): Boolean =
        info.reason == android.app.ApplicationExitInfo.REASON_LOW_MEMORY ||
            info.reason == android.app.ApplicationExitInfo.REASON_EXCESSIVE_RESOURCE_USAGE

    private fun describeExit(info: android.app.ApplicationExitInfo): String = when (info.reason) {
        android.app.ApplicationExitInfo.REASON_LOW_MEMORY ->
            "the system stopped the build process to reclaim memory"
        android.app.ApplicationExitInfo.REASON_SIGNALED -> when (info.status) {
            11 -> "the build process crashed (SIGSEGV)"
            6 -> "the build process aborted (SIGABRT)"
            9 -> "the build process was killed (SIGKILL)"
            else -> "the build process was signalled (${info.status})"
        }
        android.app.ApplicationExitInfo.REASON_CRASH -> "the build process crashed"
        android.app.ApplicationExitInfo.REASON_CRASH_NATIVE -> "the build process crashed in native code"
        android.app.ApplicationExitInfo.REASON_ANR -> "the build process stopped responding"
        android.app.ApplicationExitInfo.REASON_EXCESSIVE_RESOURCE_USAGE ->
            "the system stopped the build process for using too much of the device"
        android.app.ApplicationExitInfo.REASON_EXIT_SELF -> "the build process exited (${info.status})"
        else -> "the build process ended (reason ${info.reason}, status ${info.status})"
    }

    private fun gb(bytes: Long): String = String.format(java.util.Locale.US, "%.1f GB", bytes / 1024.0 / 1024.0 / 1024.0)

    /**
     * What the device has left, in the words a bug report needs.
     *
     * Logged where a run begins as well as where one dies: a single reading
     * taken after the fact says nothing about whether the device was already
     * against the wall, and "4.7 GB is needed" is only meaningful next to what
     * was actually free when the work started.
     */
    fun memory(context: Context): String = runCatching {
        val am = context.getSystemService(Context.ACTIVITY_SERVICE) as ActivityManager
        val mi = ActivityManager.MemoryInfo()
        am.getMemoryInfo(mi)
        val pressure = if (mi.lowMemory) ", and the device is short of memory" else ""
        "${gb(mi.availMem)} of ${gb(mi.totalMem)} free$pressure"
    }.getOrDefault("free memory unknown")

    /** How long the platform is given to write the record of a death. */
    private const val EXIT_REASON_TRIES = 6
    private const val EXIT_REASON_WAIT_MS = 250L

    /**
     * Ends every process of this app that is not in the foreground, the
     * builders included.
     *
     * The bluntest thing here, and no longer used for anything but a
     * straggler. Cancelling a run goes down the binding instead -- see the
     * cancellation handler in [exec] -- because that reaches the one process
     * that should go, and this reaches :launcher too, which is where the build
     * is being run from and which is in the background in exactly the case
     * worth surviving.
     *
     * [awaitBuilderGone] is what is left, and only while :launcher is in the
     * foreground and so out of its reach. Anywhere else it would be a bug.
     *
     * Needs KILL_BACKGROUND_PROCESSES, which is a normal permission: granted
     * at install, no prompt. If it is somehow refused, the worst case is a
     * straggler outliving the run that made it.
     */
    private fun killBuilder(context: Context) {
        val am = context.getSystemService(Context.ACTIVITY_SERVICE) as? ActivityManager ?: return
        runCatching { am.killBackgroundProcesses(context.packageName) }
    }

    /**
     * How long a builder is allowed to not exist before it counts as dead.
     *
     * Only has to cover the gap between the run being asked for and the
     * process appearing, which is a process fork; a second is generous.
     */
    private const val STARTUP_GRACE_MS = 4_000L

    /**
     * How long a builder process is given to come up once the activity manager
     * has taken the binding.
     *
     * Deliberately generous, because a fork is not the slowest thing being
     * waited for. When the previous run's process is still being reaped, the
     * activity manager holds the new start until it has been, and allows ten
     * seconds before giving up on it -- so anything shorter than that reports
     * a failure the platform had not finished deciding on. Four attempts over
     * three seconds is what this used to be, and on a device that reaps slowly
     * it gave up while the start it had asked for was still pending.
     */
    private const val CONNECT_TIMEOUT_MS = 20_000L

    /**
     * How many times a refused start is asked for again, and the pause between.
     *
     * Each attempt takes the other process, so a second attempt asks a
     * genuinely different question rather than the same one again. Three of
     * them is around a minute at worst, which is nothing against a conversion
     * that takes half an hour -- and everything against losing one.
     */
    private const val START_ATTEMPTS = 3
    private const val START_RETRY_MS = 1_000L

    /**
     * How long a straggler in the process about to be used is given to go.
     *
     * Normally nothing is waited for at all: a slot only comes round again
     * after a whole run has happened in the other one. The bound is here for
     * the process that will not go, and it is what that costs before it is
     * killed -- so it is deliberately short.
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
