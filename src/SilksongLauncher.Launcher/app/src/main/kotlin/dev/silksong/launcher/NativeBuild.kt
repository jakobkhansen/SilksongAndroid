// NativeBuild — compiling libil2cpp.so on the phone.
//
// The last step of the chain: 1500-odd translation units of generated C++ and
// IL2CPP runtime source, compiled and linked into the library the engine
// dlopens. About seventeen minutes cold on eight cores.
//
// The compile itself is tools/ondevice-il2cpp/build-il2cpp.sh, run rather than
// reimplemented. Its flags were recovered from Unity's own Bee build graph and
// are not guessable -- BASELIB_INLINE_NAMESPACE, the amalgamated bdwgc,
// Z_PREFIX on zlib, brotli needing libil2cpp on its include path -- and every
// one of them fails in a way that points somewhere else. Having two copies of
// that knowledge, one in shell for a terminal and one in Kotlin for the app,
// is how they drift; the script is staged into assets from the same file the
// terminal runs, and this class supplies it with a directory.
//
// That directory is the only real work here. The pieces are already on the
// device in three places, for reasons that are not negotiable: the toolchain
// on internal storage, because that is the only kind Android will exec from;
// the generated C++ and Unity's sources on external, because a build needs
// several gigabytes and internal storage is the scarce kind. Rather than copy
// any of it, they are linked into one root -- which is why the script uses
// find -L.

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

object NativeBuild {

    data class Progress(val step: String, val fraction: Float, val detail: String = "")

    private const val SCRIPT_ASSET = "ondevice/build-il2cpp.sh"

    /** The built library, which is what the engine loads. */
    fun output(root: File): File = File(root, "libil2cpp.so")

    fun isPresent(root: File): Boolean = output(root).length() > 0

    /**
     * Roughly how many objects the build produces, for the progress bar.
     *
     * Only used to turn a count into a fraction; being wrong makes the bar
     * inaccurate, not the build. The phases report their real totals as they
     * start, and this is replaced by those.
     */
    private const val EXPECTED_OBJECTS = 1550

    /**
     * Compiles and links libil2cpp.so.
     *
     * [root] is where the conversion left cpp/ and where obj/ and the output
     * will go -- several gigabytes, so external storage.
     */
    fun build(
        unity: File,
        toolchain: File,
        root: File,
        assets: android.content.res.AssetManager,
        install: File? = null,
    ): Flow<Progress> =
        channelFlow {
            if (!Il2cppConverter.cppDir(root).isDirectory) {
                throw IOException("nothing to compile: run the conversion first")
            }

            send(Progress("Preparing the build", -1f, "locating the sources"))
            val (script, pieces) = stage(unity, toolchain, root, assets)

            val objDir = File(root, "obj")
            // What the current phase is, and how to count its objects. The
            // script announces each with a ### line carrying the real
            // translation-unit count, and names its objects with a per-phase
            // prefix -- g for the generated C++, c for the generated C, r for
            // the runtime.
            //
            // Atomics because these genuinely cross threads: the exec callback
            // writes them as output arrives, the ticker below reads them on
            // its own schedule.
            val phase = java.util.concurrent.atomic.AtomicReference("Compiling")
            val total = java.util.concurrent.atomic.AtomicInteger(EXPECTED_OBJECTS)
            val prefix = java.util.concurrent.atomic.AtomicReference<String?>(null)

            // Progress comes from counting finished objects on a timer, not
            // from the compiler, which says nothing at all for sixteen
            // minutes. Polling rather than parsing is also what makes the
            // count honest: it measures work that has actually landed on
            // disk.
            val ticker = launch(Dispatchers.IO) {
                var last = -1
                while (isActive) {
                    val p = prefix.get()
                    val n = if (p == null) 0
                    else objDir.list()?.count { it.startsWith(p) && it.endsWith(".o") } ?: 0
                    if (n != last) {
                        last = n
                        val t = total.get()
                        val f = if (t > 0) (n.toFloat() / t).coerceIn(0f, 1f) else -1f
                        trySend(Progress(phase.get(), f, "$n of $t"))
                    }
                    delay(1000)
                }
            }

            val started = System.currentTimeMillis()
            val log = File(root, "compile.log")
            val sink = log.bufferedWriter()
            val result = try {
                Toolchain.exec(
                    listOf("/system/bin/sh", script.absolutePath),
                    cwd = root,
                    // ROOT is where the build's own output goes; the rest name
                    // the inputs, which live in other storage areas. See stage.
                    env = Toolchain.environment(toolchain) +
                        mapOf("ROOT" to root.absolutePath) + pieces,
                ) { line ->
                    // Flushed per line, not per buffer. The script emits only
                    // a couple of dozen lines over sixteen minutes, so this
                    // costs nothing, and it means a build that is killed --
                    // by the user, or by Android reclaiming the process --
                    // still leaves behind everything it had said.
                    sink.write(line); sink.write("\n"); sink.flush()
                    if (line.startsWith("###")) {
                        val text = line.removePrefix("###").trim()
                        // Only real phases reach the screen. The script also
                        // announces the device and the compiler version on
                        // ### lines, which belong in the log and not in front
                        // of somebody waiting for a progress bar -- "clang
                        // version 21.1.8" is not a thing that is happening.
                        val named = when {
                            text.startsWith("PHASE A") -> "Compiling the game" to "g"
                            text.startsWith("PHASE B") -> "Compiling the game" to "c"
                            text.startsWith("PHASE C:") -> "Compiling the engine runtime" to "r"
                            text.startsWith("PHASE C") -> "Compiling support libraries" to null
                            text.startsWith("PHASE D") -> "Linking the engine" to null
                            else -> null
                        } ?: return@exec
                        Regex("\\((\\d+) TUs\\)").find(text)?.groupValues?.get(1)?.toIntOrNull()
                            ?.let { total.set(it) }
                        phase.set(named.first)
                        prefix.set(named.second)
                        if (named.second == null) trySend(Progress(named.first, -1f, ""))
                    }
                }
            } finally {
                ticker.cancel()
                sink.flush(); sink.close()
            }
            val seconds = (System.currentTimeMillis() - started) / 1000

            val out = output(root)
            if (!result.ok || out.length() <= 0) {
                val why = result.why { it.contains("error:") }
                throw IOException("the compile failed after ${seconds}s: ${why.trim().take(300)}")
            }
            LauncherLog.log("libil2cpp.so: ${out.length()} bytes in ${seconds}s")

            if (install != null) {
                send(Progress("Installing the engine", -1f, "${out.length() / 1024 / 1024} MB"))
                installTo(out, File(install, out.name))
            }
            send(Progress("Compiled", 1f, "${out.length() / 1024 / 1024} MB in ${seconds}s"))
        }.flowOn(Dispatchers.IO)

    /**
     * Moves the built library somewhere it can actually be loaded.
     *
     * It is built on external storage because that is where there is room for
     * three gigabytes of objects, and it cannot be loaded from there: bionic
     * refuses with "not accessible for the namespace (default)" -- external
     * storage is not on any permitted path list, whatever the file's mode
     * says. So the finished library is copied onto internal storage, which is
     * where GameActivity's addNativeLibraryPath points the engine.
     *
     * Written to a temporary name and renamed, because this is 293 MB onto a
     * phone: an interrupted copy that kept the final name would be a
     * truncated ELF that the engine would try to load.
     */
    private fun installTo(from: File, to: File) {
        to.parentFile?.mkdirs()
        if (to.length() == from.length()) return
        val tmp = File(to.parentFile, "${to.name}.part")
        from.inputStream().use { i -> tmp.outputStream().use { o -> i.copyTo(o, 1 shl 20) } }
        if (!tmp.renameTo(to)) {
            tmp.delete()
            throw IOException("could not install the engine to $to")
        }
        to.setExecutable(true, true)
        LauncherLog.log("installed ${to.name} to ${to.parent}")
    }

    /**
     * Points the script at the pieces, wherever they actually are.
     *
     * These live in three storage areas -- the toolchain on internal storage
     * because that is the only kind Android will exec from, the generated C++
     * and Unity's sources on external because that is where there is room --
     * and the script wants them together.
     *
     * It used to be done with symlinks in $ROOT, which is nicer to poke at by
     * hand and is not portable: $ROOT is on external storage, that is
     * FUSE-backed, and FUSE returns EPERM for symlink() on most Android
     * builds. Some vendor ROMs permit it, which is why this worked on an AYN
     * Thor and failed on a Retroid Pocket Flip 2 with
     * "symlink failed: EPERM (Operation not permitted)".
     *
     * Naming the paths costs nothing and works everywhere.
     */
    private fun stage(
        unity: File,
        toolchain: File,
        root: File,
        assets: android.content.res.AssetManager,
    ): Pair<File, Map<String, String>> {
        root.mkdirs()
        val il2cppSrc = File(unity, "editor/Editor/Data/il2cpp")
        val pieces = mapOf(
            "USR" to File(toolchain, "usr"),
            "SYSROOT" to File(toolchain, "sysroot"),
            "LIBIL2CPP" to File(il2cppSrc, "libil2cpp"),
            "EXTERNAL" to File(il2cppSrc, "external"),
            "BASELIB" to File(
                unity,
                "android/Variations/il2cpp/Release/StaticLibs/arm64-v8a/baselib.a",
            ),
            "CPPDIR" to Il2cppConverter.cppDir(root),
        )
        for ((name, target) in pieces) {
            if (!target.exists()) throw IOException("the build needs $name, which is not at $target")
        }

        // Copied out of the APK on every run so that updating the app updates
        // the build, which is the point of keeping one source of truth.
        val script = File(root, "build-il2cpp.sh")
        assets.open(SCRIPT_ASSET).use { input ->
            script.outputStream().use { out -> input.copyTo(out) }
        }
        return script to pieces.mapValues { it.value.absolutePath }
    }
}
