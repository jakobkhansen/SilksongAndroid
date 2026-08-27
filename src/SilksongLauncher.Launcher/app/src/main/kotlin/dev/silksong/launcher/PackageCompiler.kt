// PackageCompiler — building Unity packages for Android, on the device.
//
// A Unity package ships as SOURCE. Whoever builds a player compiles it for
// that target, which is why there is no Android build of one to download:
// the package tarball carries no assemblies at all, and neither does the
// Editor install. The depot has a copy, but the depot is a desktop build.
//
// For Unity.InputSystem that difference is the whole game's input. The
// desktop assembly carries LinuxSupport and SDLDeviceBuilder; AndroidGamepad,
// XboxOneGamepadAndroid and AndroidSupport.Initialize are compiled out
// entirely. Android reports the handheld's controls perfectly well -- as an
// ordinary Xbox Wireless Controller -- and the code that would recognise one
// is simply not in the assembly, so nothing arrives.
//
// Comparing the depot's assemblies against an Android player build showed
// InputSystem to be the only package that diverges at all. Addressables,
// ResourceManager, Burst, Mathematics, ForUI and ScriptableBuildPipeline are
// platform-agnostic and work exactly as the depot ships them. So this is one
// assembly, not a category of maintenance.
//
// The compiler is Roslyn, from NuGet rather than from Unity. Unity does ship
// one, in the editor archive at Editor/Data/DotNetSdkRoslyn, and taking it
// from there was the first attempt -- but that means streaming more of a
// 4.29 GB archive to reach a tree whose position in it is not ours to
// control, and it makes a C# compiler into another thing Unity has to keep
// where we found it. Roslyn is Microsoft's, published as an ordinary NuGet
// package, and the five files that make up a working csc come to 9.6 MB.
// It runs on the .NET that MonoRuntime ships.

package dev.silksong.launcher

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.channelFlow
import kotlinx.coroutines.flow.flowOn
import java.io.BufferedInputStream
import java.io.File
import java.io.IOException
import java.security.MessageDigest
import java.util.zip.ZipInputStream

object PackageCompiler {

    data class Progress(val step: String, val fraction: Float, val detail: String = "")

    private const val ASSEMBLY = "Unity.InputSystem.dll"

    // ── Roslyn ─────────────────────────────────────────────────────────────

    private const val ROSLYN_VERSION = "4.12.0"
    private const val ROSLYN_URL =
        "https://api.nuget.org/v3-flatcontainer/microsoft.net.compilers.toolset/" +
            "$ROSLYN_VERSION/microsoft.net.compilers.toolset.$ROSLYN_VERSION.nupkg"
    private const val ROSLYN_BYTES = 21_775_071L

    /**
     * What a working csc needs, out of the 21.7 MB package.
     *
     * The rest is Visual Basic, the MSBuild task assemblies, a .NET Framework
     * copy of everything, and localized resources for a dozen languages --
     * none of which a C# compile touches. Taking five files makes it 9.6 MB.
     */
    private val ROSLYN_FILES = listOf(
        "csc.dll", "csc.deps.json", "csc.runtimeconfig.json",
        "Microsoft.CodeAnalysis.dll", "Microsoft.CodeAnalysis.CSharp.dll",
    )
    private const val ROSLYN_PREFIX = "tasks/netcore/bincore/"

    private fun roslynDir(root: File) = File(root, "roslyn")

    /**
     * Fetches Roslyn if it is not already here.
     *
     * The package is a zip, but it is read as a stream rather than seeked:
     * at 21.7 MB the whole thing costs less than the two ranged requests that
     * finding the central directory would take, and the wanted files sit in
     * the middle of it anyway.
     */
    private suspend fun stageRoslyn(root: File, onProgress: (Long, Long) -> Unit): File {
        val dir = roslynDir(root)
        val csc = File(dir, "csc.dll")
        if (ROSLYN_FILES.all { File(dir, it).length() > 0 }) return csc

        dir.mkdirs()
        var seen = 0L
        ToolchainFetcher.open(ROSLYN_URL).use { raw ->
            ZipInputStream(BufferedInputStream(raw, 1 shl 16)).use { zip ->
                while (true) {
                    val entry = zip.nextEntry ?: break
                    val name = entry.name
                    if (!name.startsWith(ROSLYN_PREFIX)) continue
                    val leaf = name.removePrefix(ROSLYN_PREFIX)
                    if (leaf !in ROSLYN_FILES) continue
                    File(dir, leaf).outputStream().use { out -> zip.copyTo(out, 1 shl 16) }
                    seen += File(dir, leaf).length()
                    onProgress(seen, 9_617_787L)
                }
            }
        }
        val missing = ROSLYN_FILES.filter { File(dir, it).length() <= 0 }
        if (missing.isNotEmpty()) throw IOException("Roslyn is incomplete: $missing")
        return csc
    }

    /** Where the compiled overrides go, for Il2cppConverter to prefer. */
    fun outputDir(root: File): File = File(root, "packages")

    fun isPresent(root: File): Boolean = File(outputDir(root), ASSEMBLY).length() > 0

    // ── the patches ────────────────────────────────────────────────────────

    private const val PATCHES = "SilksongPatches.dll"
    private const val PATCH_ASSETS = "ondevice/patches"

    /** Our own code, once compiled. Added to the build, not substituted into it. */
    fun patchAssembly(root: File): File = File(outputDir(root), PATCHES)

    fun patchesPresent(root: File): Boolean = patchAssembly(root).length() > 0

    /**
     * Compiles the port's own game code.
     *
     * Unlike the Input System this is ours, and it is compiled against the
     * user's depot rather than stock Unity alone -- which is the whole reason
     * it is built here rather than shipped as a DLL. The patches can then
     * reference the game's own types directly instead of reaching them by
     * reflection, so a signature that has moved is a compile error rather
     * than a null at runtime.
     */
    fun compilePatches(
        unity: File,
        depot: File,
        context: android.content.Context,
        root: File,
        assets: android.content.res.AssetManager,
    ): Flow<Progress> = channelFlow {
        send(Progress("Compiling the patches", -1f, ""))
        val csc = stageRoslyn(root) { done, total ->
            trySend(Progress("Fetching Roslyn", done.toFloat() / total, ToolchainFetcher.mb(done, total)))
        }
        val src = File(root, "patches").apply { deleteRecursively(); mkdirs() }
        copyAssets(assets, PATCH_ASSETS, src)

        val cs = File(src, "src").walkTopDown()
            .filter { it.isFile && it.extension == "cs" }
            .map { it.absolutePath }
            .toList()
        if (cs.isEmpty()) throw IOException("no patch sources in the APK")

        val out = patchAssembly(root)
        out.parentFile.mkdirs()
        val rsp = File(root, "patches.rsp")
        rsp.printWriter().use { w ->
            w.println("-target:library")
            w.println("-out:\"${out.absolutePath}\"")
            w.println("-optimize+")
            w.println("-nostdlib+")
            w.println("-noconfig")
            w.println("-langversion:9.0")
            w.println("-deterministic+")
            w.println("-nowarn:0169,0414,0649")
            w.println("-define:UNITY_ANDROID;ENABLE_INPUT_SYSTEM")
            // Everything: the class library, the Android engine, and the
            // depot's own assemblies. The last of those is what a prebuilt
            // DLL could never have had.
            for (r in patchReferences(unity, depot)) w.println("-reference:\"${r.absolutePath}\"")
            for (f in cs) w.println("\"$f\"")
        }

        val result = MonoRuntime.exec(
            context,
            csc,
            listOf("@${rsp.absolutePath}"),
            cwd = root,
        )
        if (!result.ok || out.length() <= 0) {
            val errors = result.output.lineSequence().filter { it.contains("error CS") }.toList()
            throw IOException(
                "the patches did not compile (${errors.size} errors): " +
                    result.why { it.contains("error CS") }.trim().take(300),
            )
        }
        LauncherLog.log("SilksongPatches.dll: ${out.length()} bytes from ${cs.size} sources")
        send(Progress("Patches ready", 1f, ""))
    }.flowOn(Dispatchers.IO)

    /** What Unity is told to call, and when. Shipped beside the sources. */
    fun entryPoints(root: File): String? =
        File(root, "patches/entrypoints.json").takeIf { it.isFile }?.readText()

    /**
     * The patches see everything: class library, Android engine, and the
     * depot.
     *
     * Name collisions resolve the same way as everywhere else -- the class
     * library and the Android engine beat the depot's desktop copies -- and
     * the Input System we rebuilt beats the depot's, so the patches compile
     * against the same assembly the game will actually run with.
     */
    private fun patchReferences(unity: File, depot: File): List<File> {
        val out = ArrayList<File>()
        val seen = HashSet<String>()
        val dirs = listOfNotNull(
            File(unity, "editor/Editor/Data/MonoBleedingEdge/lib/mono/unityaot-linux"),
            File(unity, "android/Variations/il2cpp/Managed"),
            PlayerImage.depotData(depot)?.let { File(it, "Managed") },
        )
        for (dir in dirs) {
            for (f in dir.listFiles().orEmpty()) {
                if (f.isFile && f.extension == "dll" && seen.add(f.name)) out += f
            }
        }
        return out
    }

    private fun copyAssets(assets: android.content.res.AssetManager, path: String, dest: File) {
        val entries = assets.list(path).orEmpty()
        if (entries.isEmpty()) {
            dest.parentFile?.mkdirs()
            assets.open(path).use { input -> dest.outputStream().use { input.copyTo(it) } }
            return
        }
        dest.mkdirs()
        for (name in entries) copyAssets(assets, "$path/$name", File(dest, name))
    }

    /**
     * The defines Unity would use for an Android player build.
     *
     * Two groups. The version ladder is what Unity defines for every compile,
     * and the package's own code tests it -- UNITY_6000_0_OR_NEWER and the
     * rest. The UNITY_INPUT_SYSTEM_* set comes from versionDefines in
     * Unity.InputSystem.asmdef, which maps "this package is present at this
     * version" to a define; they are listed here rather than derived because
     * the packages they name are all ones the depot demonstrably has.
     *
     * UNITY_EDITOR is deliberately absent, and that is what makes this a
     * player build: it drops the entire editor half of the package, which is
     * most of the source and none of the runtime.
     */
    private fun defines(): List<String> {
        val out = mutableListOf(
            "UNITY_ANDROID", "UNITY_ANDROID_API", "ENABLE_INPUT_SYSTEM",
            "UNITY_INPUT_SYSTEM_ENABLE_UI",
            "UNITY_INPUT_SYSTEM_ENABLE_PHYSICS",
            "UNITY_INPUT_SYSTEM_ENABLE_PHYSICS2D",
            "UNITY_INPUT_SYSTEM_ENABLE_XR",
            "UNITY_INPUT_SYSTEM_ENABLE_VR",
            "UNITY_INPUT_SYSTEM_ENABLE_ANALYTICS",
            "HAS_SET_LOCAL_POSITION_AND_ROTATION",
            "UNITY_INPUT_SYSTEM_PROJECT_WIDE_ACTIONS",
            "UNITY_INPUT_SYSTEM_INPUT_ACTIONS_EDITOR_AUTO_SAVE_ON_FOCUS_LOST",
            "UNITY_INPUT_SYSTEM_PLATFORM_SCROLL_DELTA",
            "UNITY_INPUT_SYSTEM_INPUT_MODULE_SCROLL_DELTA",
            "UNITY_INPUT_SYSTEM_SENDPOINTERHOVERTOPARENT",
            "ENABLE_VR", "ENABLE_XR", "ENABLE_MONO", "NET_STANDARD_2_1", "NET_STANDARD",
        )
        // UNITY_2017_1_OR_NEWER .. UNITY_6000_0_OR_NEWER, the way Unity emits
        // them: every version at or below the one being built with.
        for (year in 2017..2023) {
            for (stream in 1..3) out += "UNITY_${year}_${stream}_OR_NEWER"
            out += "UNITY_${year}_OR_NEWER"
        }
        out += "UNITY_6000_0_OR_NEWER"
        out += "UNITY_6000_OR_NEWER"
        return out
    }

    /**
     * Compiles Unity.InputSystem for Android.
     *
     * [unity] is the fetch root, holding both the editor tree and the package
     * source; [depot] supplies the assemblies the package references that the
     * engine does not carry, UnityEngine.UI among them.
     */
    fun compile(unity: File, depot: File, context: android.content.Context, root: File): Flow<Progress> = channelFlow {
        val pkg = UnityFetcher.packageDir(unity)
        val sources = File(pkg, "InputSystem")
        if (!sources.isDirectory) throw IOException("the Input System source is missing: $sources")

        send(Progress("Fetching Roslyn", 0f, "the C# compiler"))
        val csc = stageRoslyn(root) { done, total ->
            trySend(Progress("Fetching Roslyn", done.toFloat() / total, ToolchainFetcher.mb(done, total)))
        }

        // Which sources belong to this assembly.
        //
        // An .asmdef marks an assembly boundary: sources beneath a nested one
        // belong to THAT assembly. Plugins/InputForUI is the case that
        // matters -- it is Unity.InputSystem.ForUI, which the depot ships
        // separately, and compiling it in here fails on engine types it is
        // granted access to and we are not ("'Event' is inaccessible due to
        // its protection level").
        //
        // Everything else is taken, Editor/ included, because there is no
        // .asmdef under it: those files are part of this assembly and guard
        // their own editor-only content with #if UNITY_EDITOR. Excluding the
        // folder instead looks equivalent and is not -- runtime files have an
        // unconditional "using UnityEngine.InputSystem.Editor", and the
        // namespace it names is declared in there.
        val nested = sources.walkTopDown()
            .filter { it.isFile && it.extension == "asmdef" && it != File(sources, "Unity.InputSystem.asmdef") }
            .map { it.parentFile.absolutePath + File.separator }
            .toList()
        val cs = sources.walkTopDown()
            .filter { it.isFile && it.extension == "cs" }
            .filterNot { f -> nested.any { f.absolutePath.startsWith(it) } }
            .map { it.absolutePath }
            .toList()
        if (cs.isEmpty()) throw IOException("no sources under $sources")
        LauncherLog.log("Input System: ${cs.size} sources, ${nested.size} nested assemblies excluded")

        val refs = references(unity, depot)
        val out = File(outputDir(root), ASSEMBLY)
        out.parentFile.mkdirs()

        send(Progress("Compiling the Input System", -1f, "${cs.size} sources"))

        // The argument list is thousands of paths long, so it goes in a
        // response file -- which is Roslyn's own mechanism for exactly this,
        // and avoids depending on how long an argument list the kernel will
        // take.
        val rsp = File(root, "inputsystem.rsp")
        rsp.printWriter().use { w ->
            w.println("-target:library")
            w.println("-out:\"${out.absolutePath}\"")
            w.println("-optimize+")
            w.println("-nostdlib+")
            w.println("-noconfig")
            // The package sets allowUnsafeCode in its asmdef, and its state
            // buffers genuinely need it.
            w.println("-unsafe+")
            w.println("-langversion:9.0")
            w.println("-deterministic+")
            // Warnings are not useful here and there are a great many of them
            // -- this is somebody else's source, compiled exactly as shipped.
            w.println("-nowarn:0169,0414,0649,3021,0067")
            w.println("-define:${defines().joinToString(";")}")
            for (r in refs) w.println("-reference:\"${r.absolutePath}\"")
            for (f in cs) w.println("\"$f\"")
        }

        val log = File(root, "inputsystem.log")
        val sink = log.bufferedWriter()
        val result = try {
            MonoRuntime.exec(
            context,
            csc,
            listOf("@${rsp.absolutePath}"),
            cwd = root,
        ) { line ->
                sink.write(line); sink.write("\n")
                if (line.contains("error CS")) trySend(Progress("Compiling the Input System", -1f, line.take(90)))
            }
        } finally {
            sink.flush(); sink.close()
        }

        if (!result.ok || out.length() <= 0) {
            val errors = result.output.lineSequence().filter { it.contains("error CS") }.toList()
            throw IOException(
                "the Input System did not compile (${errors.size} errors): " +
                    result.why { it.contains("error CS") }.trim().take(300),
            )
        }
        LauncherLog.log("Unity.InputSystem.dll: ${out.length()} bytes from ${cs.size} sources")
        send(Progress("Input System ready", 1f, "${out.length() / 1024} KB"))
    }.flowOn(Dispatchers.IO)

    /**
     * What the package compiles against.
     *
     * The class library comes from the unityaot profile rather than the
     * depot's Mono one, for the same reason the IL2CPP input set does: it is
     * the profile the player actually runs. The engine assemblies come from
     * the ANDROID player, so platform-conditional engine API resolves the way
     * it will at runtime.
     *
     * From the depot, only what the asmdef actually references -- which is
     * Unity.ugui, shipped as UnityEngine.UI.dll. Handing over the whole
     * Managed folder instead was a real bug rather than mere untidiness:
     * it puts the GAME's own assemblies in scope, and Silksong's
     * Assembly-CSharp declares a ConditionalAttribute of its own. That won
     * over System.Diagnostics.ConditionalAttribute and every [Conditional]
     * in the package failed with "no argument given that corresponds to the
     * required parameter 'expectedResult'". Unity would never have offered
     * those assemblies here; the package does not depend on the game.
     */
    private fun references(unity: File, depot: File): List<File> {
        val out = ArrayList<File>()
        val seen = HashSet<String>()
        // The assembly being built is not a reference to itself. The depot
        // has a copy, and leaving it in makes every type in the source
        // collide with its own compiled twin.
        seen += ASSEMBLY
        val bcl = File(unity, "editor/Editor/Data/MonoBleedingEdge/lib/mono/unityaot-linux")
        val engine = File(unity, "android/Variations/il2cpp/Managed")
        for (dir in listOf(bcl, engine)) {
            for (f in dir.listFiles().orEmpty()) {
                if (!f.isFile || f.extension != "dll") continue
                // First wins, so the class library beats the engine's copies
                // of the same names.
                if (seen.add(f.name)) out += f
            }
        }
        // "Unity.ugui" in the asmdef, and the netstandard facade.
        //
        // netstandard is not optional: Unity's engine assemblies are built
        // against netstandard 2.1, so every type they expose that is really a
        // BCL type -- Enum, ValueType, Attribute -- is reached through that
        // facade. Without it the compile fails 3270 times over, all of them
        // "the type X is defined in an assembly that is not referenced".
        // The depot's copy is the one the game itself runs with; it merely
        // type-forwards, so it resolves against whichever mscorlib is in the
        // reference set, which here is the unityaot one.
        PlayerImage.depotData(depot)?.let { data ->
            for (name in listOf("UnityEngine.UI.dll", "netstandard.dll")) {
                val f = File(data, "Managed/$name")
                if (f.isFile && seen.add(name)) out += f
            }
        }
        if (out.none { it.name == "mscorlib.dll" }) throw IOException("no class library to compile against")
        return out
    }
}
