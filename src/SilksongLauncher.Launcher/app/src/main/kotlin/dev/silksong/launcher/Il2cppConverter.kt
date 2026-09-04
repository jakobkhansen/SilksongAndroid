// Il2cppConverter — turning the game's IL into C++, on the device.
//
// This is the step everything else was assumed to need a PC for. Unity ships
// il2cpp as a self-contained .NET application with a Linux-x64 apphost, but
// the apphost is only a launcher: il2cpp.dll beside it is portable IL. Strip
// the bundled runtime and the deps.json, and it becomes an ordinary
// framework-dependent app that any .NET can run -- including the one
// MonoRuntime ships.
//
// This once ran on a PC, as a shell script and as steps 1 and 2 of
// tools/depot-to-apk/build.sh; both are retired. A desktop run of the same
// inputs produces byte-identical output: all generated sources and
// global-metadata.dat match by SHA-256.
//
// The assembly set is the part that is easy to get wrong, and it fails
// obscurely when it is. The depot's Managed/ folder cannot be handed to
// il2cpp wholesale -- it holds the *Mono* flavour of the class library, and
// IL2CPP wants the unityaot profile. Feeding it the Mono set does not produce
// a message about profiles; it dies deep inside the converter while building
// shared enum types, saying "System.Byte, ". The composition Unity itself uses
// for a build is:
//
//   class library   Unity's unityaot-linux profile
//   UnityEngine     the *Android* player's Managed folder, not the depot's
//                   Linux one
//   game code       the depot, and only what the two above do not provide

package dev.silksong.launcher

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.channelFlow
import kotlinx.coroutines.flow.flowOn
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import java.io.File
import java.io.IOException

object Il2cppConverter {

    data class Progress(val step: String, val fraction: Float, val detail: String = "")

    /**
     * The generated C++ and the player data.
     *
     * On external storage: this is around 1.5 GB of source that nothing has to
     * execute, and internal storage is the scarce kind.
     */
    fun rootFor(context: android.content.Context): File =
        File(context.getExternalFilesDir(null), "build")

    fun cppDir(root: File): File = File(root, "cpp")
    fun dataDir(root: File): File = File(root, "data")
    fun asmDir(root: File): File = File(root, "asm")

    /** global-metadata.dat is the one output nothing else can substitute for. */
    fun metadata(root: File): File = File(dataDir(root), "Metadata/global-metadata.dat")

    /** How often the output directory is counted while il2cpp works. */
    private const val PROGRESS_POLL_MS = 2_000L

    /**
     * What the last conversion produced, and what the next one is measured
     * against.
     *
     * A count rather than a guess at the work: the set is 186 assemblies of
     * someone else's game, and nothing here can predict how much C++ that
     * becomes. It barely moves between runs, though -- the same depot and the
     * same patches produce the same files -- so the last answer is a good
     * denominator and a wrong one only costs a bar that fills unevenly.
     *
     * Kept beside the output rather than in it: [convert] empties the
     * directory before il2cpp starts, which is exactly when this is needed.
     */
    private fun sourceCount(root: File) = File(root, "cpp.count")

    private fun expectedSources(root: File): Int =
        sourceCount(root).takeIf { it.isFile }?.readText()?.trim()?.toIntOrNull()
            ?.takeIf { it > 0 } ?: DEFAULT_SOURCES

    private fun rememberSources(root: File) {
        val n = cppDir(root).list()?.size ?: return
        if (n > 0) runCatching { sourceCount(root).writeText(n.toString()) }
    }

    /**
     * The count before there has ever been one, from a Pixel 10 Pro: 950 .cpp
     * and 197 .c, plus a header. Only ever used for a first conversion, and
     * replaced by that conversion's own number.
     */
    private const val DEFAULT_SOURCES = 1148

    /**
     * Proof that a conversion ran all the way to the end.
     *
     * [isPresent] used to ask only whether global-metadata.dat and some .cpp
     * existed, and a conversion killed near the end satisfies both -- the
     * metadata lands before the last of eleven hundred sources do. So an
     * interrupted run was inherited by the next build as finished work: the
     * conversion was skipped, the compile took whatever partial tree was on
     * disk, and the link accepted it because it allows undefined symbols. The
     * result was a libil2cpp.so nine megabytes short of the real one, twelve
     * minutes later, which the engine reported at launch as
     *
     *   dlopen failed: library "libil2cpp.so" not found
     *
     * -- naming neither the file it did find nor anything that happened here.
     *
     * That is not a rare shape. The conversion is three and a half minutes of
     * a memory-hungry .NET process, and a device that reclaims the app during
     * it drops the user back on a launcher with a Play button, because the
     * build screen has already finished itself and only the launcher is
     * restored. The recovery that worked was "Reset build", which is a thing
     * somebody has to know to do.
     *
     * Written last and deleted first, so it never outlives the output it
     * describes. Same reasoning as SetupActivity's .built marker and
     * NativeBuild's .stamp, one layer down.
     */
    private fun doneMarker(root: File) = File(root, "cpp.done")

    /**
     * What a finished conversion looks like, as a string.
     *
     * The source count and the metadata's size rather than a bare "yes":
     * those also notice a tree that has been pruned, or a metadata file
     * replaced, since the run that wrote this.
     */
    private fun completionSignature(root: File): String =
        "${cppDir(root).list()?.size ?: 0}:${metadata(root).length()}"

    private fun markComplete(root: File) {
        // Failing to write it costs a conversion that did not need to happen.
        // Failing to notice it is missing costs a build that cannot run.
        runCatching { doneMarker(root).writeText(completionSignature(root)) }
    }

    /**
     * Whether the conversion on disk is one that finished.
     *
     * A build made before this marker existed does not carry one and converts
     * again, once. That costs about four minutes -- the compile that follows
     * is incremental and re-generated C++ is byte-identical, so almost
     * nothing is rebuilt -- and it is the direction to be wrong in.
     */
    fun isComplete(root: File): Boolean =
        runCatching { doneMarker(root).readText().trim() }.getOrNull() ==
            completionSignature(root)

    fun isPresent(root: File): Boolean =
        isComplete(root) &&
            metadata(root).length() > 0 &&
            cppDir(root).listFiles()?.any { it.name.endsWith(".cpp") } == true

    /**
     * Whether the conversion is older than what it was made from.
     *
     * Only our own assemblies and the mods folder are checked, because only
     * they change without anything else changing: the depot is fixed and the
     * Input System is rebuilt from a pinned version, but the port's own code
     * is edited between builds and a plugin can be dropped in at any time.
     * Without this a changed patch compiles happily, is copied into the
     * assembly set, and is then skipped by a conversion that thinks it has
     * nothing to do -- so the player keeps running the previous version and
     * nothing says otherwise.
     *
     * BOTH of ours, and that is not a tidiness point. SilksongIo arrived in a
     * release whose patch sources had not changed at all, so a check that
     * looked only at SilksongPatches said "nothing to do" and skipped the
     * conversion that applies the File.Replace redirect -- which would have
     * shipped a release whose headline fix reached nobody who already had a
     * build. An assembly of ours that is missing from the staged set counts as
     * stale for the same reason: that is exactly what an upgrade looks like.
     */
    fun isStale(
        root: File,
        mods: File? = null,
        assets: android.content.res.AssetManager? = null,
    ): Boolean {
        if (mods != null && Mods.isStale(mods, root, assets)) return true
        val ours = listOf(PackageCompiler.patchAssembly(root), PackageCompiler.ioAssembly(root)) +
            PackageCompiler.shimAssemblies(root)
        for (built in ours) {
            if (!built.isFile) continue
            val staged = File(asmDir(root), built.name)
            if (!staged.isFile) return true
            // Content, not length. Two builds of the patches differ in what
            // they do far more often than in how big they are, and a same-size
            // assembly read as "unchanged" means the conversion is skipped, the
            // old generated C++ is recompiled, and the device runs the previous
            // version of a patch while every log line says the build succeeded.
            // That is not hypothetical: it cost three rounds of chasing a bug
            // that had already been fixed.
            if (staged.length() != built.length()) return true
            if (!staged.readBytes().contentEquals(built.readBytes())) return true
        }
        return false
    }

    // ── inputs ─────────────────────────────────────────────────────────────

    private fun bclDir(unity: File) =
        File(unity, "editor/Editor/Data/MonoBleedingEdge/lib/mono/unityaot-linux")

    private fun engineManagedDir(unity: File) =
        File(unity, "android/Variations/il2cpp/Managed")

    private fun deployDir(unity: File) =
        File(unity, "editor/Editor/Data/il2cpp/build/deploy")

    /**
     * The depot's Managed folder.
     *
     * Off [PlayerImage.depotData] rather than looked for separately: the depot
     * may be nested under folders a person copied it inside, and two different
     * ideas of where it is means one of them finds a hand-placed copy and the
     * other does not.
     */
    private fun depotManaged(depot: File): File? =
        PlayerImage.depotData(depot)?.let { File(it, "Managed") }
            ?.takeIf { File(it, "Assembly-CSharp.dll").isFile }

    // ── the run ────────────────────────────────────────────────────────────

    fun convert(
        unity: File,
        depot: File,
        context: android.content.Context,
        root: File,
        mods: File? = null,
        assets: android.content.res.AssetManager? = null,
    ): Flow<Progress> = channelFlow {
        val bcl = bclDir(unity)
        val engine = engineManagedDir(unity)
        val deploy = deployDir(unity)
        val managed = depotManaged(depot)
            ?: throw IOException("no Managed folder with Assembly-CSharp.dll under $depot")
        // Last line of defence, and the one that names the cause. Everything
        // above this can be reached by a depot that was accepted before the
        // platform was ever checked -- an app updated mid-build, a pointer
        // written by an older version -- and the failure without it is the
        // one this check exists because of: four minutes of work, then il2cpp
        // dying on two class libraries at once with nothing readable to say.
        PlayerImage.wrongBuildProblem(depot)?.let { throw IOException(it) }
        if (!bcl.isDirectory) throw IOException("the unityaot class library is missing: $bcl")
        if (!engine.isDirectory) throw IOException("the Android player's Managed folder is missing: $engine")
        if (!File(deploy, "il2cpp.dll").isFile) throw IOException("il2cpp.dll is missing: $deploy")

        send(Progress("Preparing the converter", -1f, "assemblies"))
        var assemblies = stageAssemblies(bcl, engine, managed, PackageCompiler.outputDir(root), asmDir(root))
        LauncherLog.log("il2cpp input: ${assemblies.size} assemblies")

        redirectSaveCalls(context, root)

        // The port's own weaves, before the user's and regardless of whether
        // there are any: these ship with the build, so the empty mods folder
        // that skips everything below still gets them.
        if (assets != null) {
            send(Progress("Patching the game", -1f, "built-in weaves"))
            Mods.weaveBuiltin(context, root, asmDir(root), assets) { line ->
                trySend(Progress("Patching the game", -1f, line.take(80)))
            }
        }

        // The chainloader, run here rather than at game startup: this is the
        // last moment the game exists as IL, so it is the only moment a
        // Harmony patch can be applied. Plugins are woven into the staged set
        // and then converted along with everything else, which is why the
        // assembly list is taken again afterwards -- a plugin il2cpp is not
        // handed is a plugin that is not in the game.
        //
        // Every plugin present is woven, not only the enabled ones. Which are
        // on is decided at startup by the gate each weave is wrapped in, so a
        // toggle costs nothing and only adding or removing a file is a
        // rebuild. See Mods.gates.
        if (mods != null && assets != null) {
            val plugins = Mods.all(mods)
            if (plugins.isNotEmpty()) {
                send(Progress("Weaving mods", -1f, "${plugins.size} plugin(s)"))
                Mods.weave(context, root, mods, asmDir(root), assets) { line ->
                    trySend(Progress("Weaving mods", -1f, line.take(80)))
                }
                assemblies = asmDir(root).listFiles().orEmpty()
                    .filter { it.name.endsWith(".dll") }.sortedBy { it.name }
                LauncherLog.log("il2cpp input after weaving: ${assemblies.size} assemblies")
            }
        }

        prepareTool(deploy)

        val argv = ArrayList<String>()
        argv += "--convert-to-cpp"
        // The command line is long -- around 185 of these -- but it is handed
        // to the runtime as an array, so the argument limit that would force a
        // shell to spill them to a file does not apply.
        for (a in assemblies) argv += "--assembly=${a.absolutePath}"
        argv += "--generatedcppdir=${cppDir(root).absolutePath}"
        argv += "--data-folder=${dataDir(root).absolutePath}"
        // Must match the class library staged above.
        argv += "--dotnetprofile=unityaot-linux"
        argv += "--emit-null-checks"
        argv += "--enable-array-bounds-check"
        argv += "--static-lib-il2-cpp"

        val expected = expectedSources(root)
        val log = File(root, "convert.log")
        val started = System.currentTimeMillis()

        // One attempt, and the machinery that makes it watchable.
        //
        // A function rather than a block because a conversion can be reclaimed
        // for memory and tried again smaller, and everything here has to be
        // done afresh when it is: the output directory is emptied, the log is
        // rewritten, and the progress counter starts from nothing.
        suspend fun attempt(budget: MonoRuntime.Budget): Toolchain.Result {
            // Any previous attempt is cleared: il2cpp is not asked to reconcile
            // a half-written tree, and a stale .cpp left behind by an
            // interrupted run would be compiled into the result.
            //
            // The marker goes first, and on its own line, so that a run killed
            // anywhere below here leaves output that says outright it is
            // unfinished rather than output the next build mistakes for work
            // it does not have to do.
            doneMarker(root).delete()
            cppDir(root).deleteRecursively()
            dataDir(root).deleteRecursively()
            cppDir(root).mkdirs()
            dataDir(root).mkdirs()

            LauncherLog.log("il2cpp: starting with $budget; ${MonoRuntime.memory(context)}")
            send(Progress("Converting to C++", 0f, "0 of $expected files"))
            val sink = log.bufferedWriter()
            // Real progress, counted off the output directory.
            //
            // il2cpp says nothing at all while it works: its stdout is block
            // buffered into a file and the whole of it arrives at once when the
            // process ends, so the line-driven detail below never fires and the
            // bar had nothing to move on. Six minutes of a full stop looks
            // exactly like a hang -- one person waited half an hour and gave up
            // on a conversion that may well have been working.
            //
            // The files themselves are the honest signal: they land steadily
            // throughout, and there is no interpretation involved in counting
            // them. The total is close to fixed for a given depot and patch set,
            // so the previous run's count is the denominator and the constant is
            // only ever used once.
            val ticker = launch(Dispatchers.IO) {
                while (isActive) {
                    delay(PROGRESS_POLL_MS)
                    val n = cppDir(root).list()?.size ?: 0
                    // Never quite full: the step is over when il2cpp says so, not
                    // when a guessed total is reached, and a bar that sits at 100%
                    // is a bar that has started lying.
                    trySend(
                        Progress(
                            "Converting to C++",
                            (n.toFloat() / expected).coerceIn(0f, 0.99f),
                            "$n of $expected files",
                        ),
                    )
                }
            }
            return try {
                MonoRuntime.exec(
                    context,
                    File(deploy, "il2cpp.dll"),
                    argv,
                    // il2cpp resolves parts of its own installation relative to
                    // the working directory.
                    cwd = deploy,
                    env = budget.toEnv(),
                ) { line ->
                    sink.write(line); sink.write("\n")
                }
            } finally {
                ticker.cancel()
                sink.flush(); sink.close()
            }
        }

        var budget = MonoRuntime.budget(context)
        var result = attempt(budget)
        // Reclaimed rather than failed: the settings were too generous for
        // this device as it stood, so the same work is offered a smaller share
        // of it. Only for that one cause -- a conversion that threw is a
        // conversion that will throw again, and retrying it costs the user
        // another six minutes to reach the same message.
        while (result.outOfMemory) {
            val next = budget.tighter() ?: break
            budget = next
            LauncherLog.log("il2cpp: reclaimed for memory; retrying with $budget")
            send(Progress("Converting to C++", 0f, "retrying with less memory"))
            result = attempt(budget)
        }
        val seconds = (System.currentTimeMillis() - started) / 1000

        if (!result.ok) {
            if (result.outOfMemory) {
                throw IOException(
                    "il2cpp ran out of memory after ${seconds}s, at the smallest settings " +
                        "there are ($budget). Close other apps, or restart the device, and " +
                        "try again -- ${MonoRuntime.memory(context)}.",
                )
            }
            val why = result.output.lineSequence()
                .firstOrNull { it.contains("rror", true) || it.contains("xception", true) }
                ?.trim()?.take(300)
                ?: lastWords(log)
            throw IOException("il2cpp failed after ${seconds}s, exit ${result.code}: $why")
        }
        if (metadata(root).length() <= 0) {
            throw IOException("il2cpp produced no global-metadata.dat")
        }

        val cpp = cppDir(root).listFiles()?.count { it.name.endsWith(".cpp") } ?: 0
        val c = cppDir(root).listFiles()?.count { it.name.endsWith(".c") } ?: 0
        rememberSources(root)
        // Only now, and only after every check above: this is what the next
        // build reads as permission to skip the four minutes it took to get
        // here, so it has to mean the run reached this line.
        markComplete(root)
        LauncherLog.log(
            "il2cpp: ${seconds}s, $cpp cpp + $c c, metadata ${metadata(root).length()} bytes",
        )
        // Only now: the stamp says "this build contains that mod set", and it
        // would be a lie if the conversion had failed anywhere above.
        if (mods != null) Mods.markCurrent(mods, root, assets)
        send(Progress("Converted", 1f, "$cpp C++ files in ${seconds}s"))
    }.flowOn(Dispatchers.IO)

    /**
     * The last thing il2cpp said, for a failure that said nothing error-shaped.
     *
     * The line above this picks the first line of output that reads like an
     * error, which is the right answer when there is one and was the only
     * answer there was. When there is not, what it fell back to was "exit
     * 120" -- and that is what a bug report on 1.0.3-rc3 consisted of, for a
     * failure whose cause was sitting unread in convert.log a directory away.
     * il2cpp does not always announce itself: fed two class libraries at once
     * it dies part-way through a sentence about a type, which contains
     * neither "error" nor "exception".
     *
     * So: the end of the log, which is where a program that stopped says why.
     * Read from the tail rather than whole -- a conversion that got some way
     * in leaves megabytes -- and the first line of that read is dropped,
     * because a seek to a byte offset lands in the middle of one.
     */
    private fun lastWords(log: File): String {
        val lines = runCatching {
            java.io.RandomAccessFile(log, "r").use { raf ->
                val from = (raf.length() - TAIL_BYTES).coerceAtLeast(0L)
                raf.seek(from)
                val buf = ByteArray((raf.length() - from).toInt())
                raf.readFully(buf)
                String(buf, Charsets.UTF_8).lines().let { if (from > 0) it.drop(1) else it }
            }
        }.getOrDefault(emptyList())
            .map { it.trim() }
            .filter { it.isNotEmpty() }
        if (lines.isEmpty()) return "it printed nothing at all, so ${log.name} is empty too"
        return lines.takeLast(TAIL_LINES).joinToString(" | ").takeLast(300)
    }

    /** How much of the converter's log a failure quotes. */
    private const val TAIL_BYTES = 8L * 1024L
    private const val TAIL_LINES = 3

    /**
     * Composes the assembly set, in the order Unity composes it.
     *
     * Later sources do not overwrite earlier ones: the class library and the
     * engine win over the depot's copies of the same names, which is the whole
     * point -- the depot carries the Linux player's UnityEngine assemblies and
     * the Mono class library, and both are wrong here.
     *
     * [packages] is different: those are Android builds of packages the depot
     * ships as desktop builds, and they REPLACE the depot's copy rather than
     * merely being preferred to it. Only names the set already has are taken,
     * because adding an unrelated assembly would change the type graph for no
     * reason.
     */
    /**
     * Points the game's File.Replace calls at SafeIo, before il2cpp sees them.
     *
     * This is the only moment it can be done. The launcher is not running
     * while the game is, and WriteSaveSlot is the depot's own compiled code --
     * so the call site is rewritten here, while it is still IL and after the
     * assemblies have been staged but before they are turned into C++.
     *
     * See tools/silksong-io/src/SafeIo.cs: File.Replace fails outright on some
     * devices, and the game commits every save and every shared-data write
     * through it. The helper tries the original call first, so on a device
     * where saving already worked this changes nothing at all.
     *
     * A failure here is logged rather than thrown, and that is a deliberate
     * trade. If a future Silksong update reshapes the save path the rewrite
     * will stop matching, and a game that builds and cannot save on SOME
     * devices is a great deal better than a game that will not build on any.
     */
    private suspend fun redirectSaveCalls(context: android.content.Context, root: File) {
        val assembly = File(asmDir(root), "Assembly-CSharp.dll")
        val helper = File(asmDir(root), PackageCompiler.IO_ASSEMBLY)
        if (!assembly.isFile) {
            LauncherLog.log("save fix: no Assembly-CSharp.dll staged; skipping the File.Replace redirect")
            return
        }
        if (!helper.isFile) {
            LauncherLog.log("save fix: ${PackageCompiler.IO_ASSEMBLY} was not staged; skipping the File.Replace redirect")
            return
        }
        try {
            val surgery = PlayerImage.stageSurgery(root, context.assets)
            PlayerImage.run(
                surgery,
                context,
                listOf("redirect-file-replace", assembly.absolutePath, helper.absolutePath),
            ) { line -> LauncherLog.log("save fix: ${line.trim()}") }
        } catch (t: Throwable) {
            LauncherLog.log("save fix: could not redirect File.Replace -- saving may fail on devices " +
                "whose storage cannot do it", t)
        }
    }

    private fun stageAssemblies(
        bcl: File,
        engine: File,
        managed: File,
        packages: File,
        out: File,
    ): List<File> {
        out.deleteRecursively()
        out.mkdirs()
        var fromBcl = 0
        var fromEngine = 0
        var fromDepot = 0
        for ((source, counter) in listOf(bcl to 0, engine to 1, managed to 2)) {
            for (dll in source.listFiles().orEmpty()) {
                if (!dll.isFile || !dll.name.endsWith(".dll")) continue
                val dst = File(out, dll.name)
                if (dst.exists()) continue
                dll.copyTo(dst, overwrite = true)
                when (counter) {
                    0 -> fromBcl++
                    1 -> fromEngine++
                    else -> fromDepot++
                }
            }
        }
        var overridden = 0
        var added = 0
        for (dll in packages.listFiles().orEmpty()) {
            if (!dll.isFile || !dll.name.endsWith(".dll")) continue
            val dst = File(out, dll.name)
            if (dst.exists()) {
                // A rebuilt copy of something the depot already has: replace
                // it, because the depot's is the desktop build.
                dll.copyTo(dst, overwrite = true)
                overridden++
            } else {
                // Something the depot does not have at all -- our own patches.
                // Added rather than substituted, and it must reach il2cpp or
                // none of the port's own code exists in the player.
                dll.copyTo(dst, overwrite = true)
                added++
            }
        }
        LauncherLog.log(
            "assemblies: $fromBcl class library, $fromEngine engine, $fromDepot from the depot, " +
                "$overridden rebuilt for Android, $added ours",
        )
        val all = out.listFiles().orEmpty().filter { it.name.endsWith(".dll") }.sortedBy { it.name }
        if (all.isEmpty()) throw IOException("no assemblies were staged into $out")
        return all
    }

    /**
     * Makes Unity's il2cpp runnable by a shared framework.
     *
     * It ships self-contained: a private CoreCLR, native hosts, and a
     * deps.json pinning both to linux-x64. None of that survives the move to
     * arm64, and none of it is needed -- il2cpp.dll is portable IL. The
     * private System.Private.CoreLib has to go too: it is version-locked to
     * the runtime it shipped with, and leaving it behind makes the shared
     * framework load a mismatched core library.
     *
     * Done in place, once, marked so a re-run is free.
     */
    private fun prepareTool(deploy: File) {
        val marker = File(deploy, ".silksong-prepared")
        if (marker.isFile) return

        val doomed = listOf(
            "System.Private.CoreLib.dll", "libcoreclr.so", "libclrjit.so", "libclrgc.so",
            "libhostfxr.so", "libhostpolicy.so", "libmscordaccore.so", "libmscordbi.so",
            "libcoreclrtraceptprovider.so", "createdump", "il2cpp", "il2cpp-compile",
        )
        for (name in doomed) File(deploy, name).delete()
        for (f in deploy.listFiles().orEmpty()) {
            val n = f.name
            if (n.endsWith(".deps.json") || n.endsWith(".pdb") ||
                (n.startsWith("libSystem.") && n.endsWith(".so"))
            ) f.delete()
        }

        // rollForward=latestMajor so this keeps working against whichever
        // runtime the device happens to carry. Invariant globalization keeps
        // ICU out of the picture -- il2cpp does not need culture data, and it
        // is 30 MB not to have to fetch.
        //
        // Inert, and kept anyway: nothing on the device reads it. hostfxr and
        // hostpolicy are the parts that would, and they are among the files
        // deleted above; the runtime is started through the hosting API with
        // the properties monojni passes it, which is where the settings that
        // actually take effect live. It is written so the deploy directory
        // describes what it is being run as, and Server GC says false here for
        // the same reason it says false there -- this collector has no server
        // mode, and a file claiming otherwise is a file that sends the next
        // person looking in the wrong place.
        File(deploy, "il2cpp.runtimeconfig.json").writeText(
            """
            {
              "runtimeOptions": {
                "tfm": "net8.0",
                "framework": { "name": "Microsoft.NETCore.App", "version": "8.0.0" },
                "rollForward": "latestMajor",
                "configProperties": {
                  "System.GC.Server": false,
                  "System.Globalization.Invariant": true,
                  "System.Globalization.PredefinedCulturesOnly": true,
                  "System.Runtime.TieredCompilation.QuickJit": false
                }
              }
            }
            """.trimIndent(),
        )
        marker.writeText("")
    }
}
