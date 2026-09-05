// Mods — the folder, and what happens to what is in it.
//
// A BepInEx plugin is an ordinary managed assembly compiled against the game's
// Mono assemblies. That is exactly what this pipeline is holding right before
// il2cpp runs, and never again afterwards -- so the chainloader runs HERE,
// between the depot being staged and the conversion starting, rather than at
// game startup the way it does on a PC.
//
// mod-weaver does the work: it reads each plugin, resolves every [HarmonyPatch]
// against the staged assemblies, and writes the prefix and postfix calls into
// the game's IL as instructions. il2cpp then compiles a game that was already
// patched. Nothing is hooked at runtime, because by runtime there is no IL
// left to hook.
//
// The cost of that is honest and unavoidable: installing a mod means a
// rebuild, not a restart. What is NOT owed is a rebuild for turning one off.
// Every plugin in the folder is woven, enabled or not, and every call the
// weaver writes is wrapped in a test of a gate field in the plugin's own
// assembly. The chainloader opens the gates of the plugins that are on. So
// the stamp below covers what is IN the folder rather than what is switched
// on: adding or replacing a file is a rebuild, flipping a switch is a file
// the game reads at startup.

package dev.silksong.launcher

import java.io.File
import java.io.IOException
import java.io.InputStream
import java.security.MessageDigest

object Mods {

    /**
     * Where a user puts plugin DLLs.
     *
     * The app's own external files directory, so it is reachable over USB and
     * from any file manager without a permission, and -- because the game runs
     * inside this same package -- it is the same path the game's own
     * BepInEx.Paths finds at runtime for configs.
     */
    fun dir(context: android.content.Context): File =
        File(context.getExternalFilesDir(null), "mods")

    fun configDir(mods: File): File = File(mods, "config")

    private fun disabledFile(mods: File): File = File(mods, "disabled.txt")

    /** Written on first use, so the folder explains itself when it is empty. */
    fun ensure(mods: File) {
        if (!mods.isDirectory) mods.mkdirs()
        configDir(mods).mkdirs()
        val readme = File(mods, "README.txt")
        if (!readme.isFile) {
            readme.writeText(
                """
                Put BepInEx 5 plugin DLLs in this folder.

                They are compiled into the game when you build, not loaded when
                you launch -- so adding or removing one means rebuilding from
                the launcher. Turning one off is free: every plugin here is
                built in, and the switch in the launcher's Mods screen decides
                at startup which of them run.

                Config files are in config/ and are read at startup, so those
                you can change freely.

                Transpilers, runtime-computed patch targets and Reflection.Emit
                cannot work here. The launcher tells you which plugins used
                them.
                """.trimIndent() + "\n",
            )
        }
    }

    // ── what is in the folder ──────────────────────────────────────────────

    /**
     * Every plugin DLL, disabled ones included.
     *
     * Searched recursively, because a mod is often distributed as a folder
     * with the plugin and its own libraries inside. config/ is skipped: a .cfg
     * is not an assembly, but nothing stops somebody dropping one in there.
     */
    fun all(mods: File): List<File> {
        if (!mods.isDirectory) return emptyList()
        val config = configDir(mods).absolutePath + File.separator
        return mods.walkTopDown()
            .filter { it.isFile && it.extension.equals("dll", ignoreCase = true) }
            .filterNot { it.absolutePath.startsWith(config) }
            .sortedBy { it.absolutePath }
            .toList()
    }

    fun relativePath(mods: File, dll: File): String =
        dll.absolutePath.removePrefix(mods.absolutePath + File.separator)

    /**
     * Plugins the user turned off.
     *
     * A list rather than a rename or a move: disabling something should not
     * touch a file somebody downloaded, and a mod that is off today is usually
     * on again next week.
     */
    fun disabled(mods: File): Set<String> {
        val f = disabledFile(mods)
        if (!f.isFile) return emptySet()
        return f.readLines().map { it.trim() }.filter { it.isNotEmpty() }.toSet()
    }

    /**
     * Turns one plugin on or off.
     *
     * [root] so that the gate list can be rewritten in the same breath: the
     * two files have to agree, and the only reason there are two is that the
     * launcher thinks in file names and the game can only think in assembly
     * names.
     */
    fun setEnabled(mods: File, root: File, relative: String, enabled: Boolean) {
        val current = disabled(mods).toMutableSet()
        if (enabled) current.remove(relative) else current.add(relative)
        val f = disabledFile(mods)
        if (current.isEmpty()) f.delete() else f.writeText(current.sorted().joinToString("\n") + "\n")
        writeGates(mods, root)
    }

    fun enabled(mods: File): List<File> {
        val off = disabled(mods)
        return all(mods).filterNot { relativePath(mods, it) in off }
    }

    // ── the gates ──────────────────────────────────────────────────────────

    /**
     * What the game reads at startup to decide which woven mods to run.
     *
     * By assembly name, because that is the only name the game has: the
     * chainloader is reflecting over loaded assemblies and has never seen the
     * mods folder. [disabledFile] stays the source of truth -- it is keyed by
     * the path the user recognises -- and this is derived from it through the
     * weaver's report, which is the one place both names appear together.
     *
     * Absent means nothing is off. That is deliberate: it is also what a build
     * from before this file existed looks like, and every plugin in such a
     * build was compiled in wanting to run.
     */
    fun gatesFile(mods: File): File = File(mods, "disabled-assemblies.txt")

    fun writeGates(mods: File, root: File) {
        val off = disabled(mods)
        val byFile = lastReport(root).associateBy { it.file }
        val names = all(mods)
            .filter { relativePath(mods, it) in off }
            .mapNotNull { byFile[it.name]?.assembly?.takeIf(String::isNotEmpty) }
            .distinct()
            .sorted()
        val f = gatesFile(mods)
        try {
            if (names.isEmpty()) f.delete() else f.writeText(names.joinToString("\n") + "\n")
            LauncherLog.log("mods: ${names.size} assembly/assemblies switched off")
        } catch (t: Throwable) {
            LauncherLog.log("mods: could not write the gate list: $t")
        }
    }

    // ── staleness ──────────────────────────────────────────────────────────

    private fun stampFile(root: File): File = File(root, "mods.stamp")

    /**
     * What the folder and the weaver that consumes it contain, by content.
     *
     * Every plugin present, not only the enabled ones: all of them are woven
     * into the build, and which are switched on is decided at startup rather
     * than at build time. A stamp over the enabled set would make every toggle
     * a twenty-minute rebuild for a change the build does not actually need.
     *
     * Content and not timestamps: a plugin replaced by a different build of
     * itself is the case that matters most, and it is also the case most
     * likely to arrive with whatever mtime the zip carried.
     */
    fun stamp(mods: File, assets: android.content.res.AssetManager? = null): String {
        val sha = MessageDigest.getInstance("SHA-256")
        val plugins = all(mods)
        for (dll in plugins) {
            sha.update(relativePath(mods, dll).toByteArray())
            dll.inputStream().use { sha.updateFrom(it) }
        }
        if (plugins.isNotEmpty() && assets != null) {
            sha.updateAssets(assets, WEAVER_ASSET_DIR)
        }
        return sha.digest().joinToString("") { "%02x".format(it) }
    }

    private fun MessageDigest.updateAssets(assets: android.content.res.AssetManager, dir: String) {
        for (name in assets.list(dir).orEmpty().sorted()) {
            val path = "$dir/$name"
            val children = assets.list(path).orEmpty()
            if (children.isNotEmpty()) {
                updateAssets(assets, path)
            } else {
                try {
                    update(path.toByteArray())
                    // Normalised, for the reason AssetDigest gives: the weaver
                    // ships a .deps.json beside its DLL, and a CRLF checkout of
                    // it made every mod look stale on a Windows-built launcher.
                    AssetDigest.update(this, assets, path)
                } catch (_: IOException) {
                }
            }
        }
    }

    private fun MessageDigest.updateFrom(input: InputStream) {
        val buf = ByteArray(1 shl 16)
        while (true) {
            val n = input.read(buf)
            if (n < 0) break
            update(buf, 0, n)
        }
    }

    /** Whether the mods folder differs from what the current build was made from. */
    fun isStale(mods: File, root: File, assets: android.content.res.AssetManager? = null): Boolean {
        val f = stampFile(root)
        val previous = if (f.isFile) f.readText().trim() else ""
        return previous != stamp(mods, assets)
    }

    fun markCurrent(mods: File, root: File, assets: android.content.res.AssetManager? = null) {
        stampFile(root).writeText(stamp(mods, assets))
        writeBuilt(mods, root)
    }

    fun clearStamp(root: File) {
        stampFile(root).delete()
        builtFile(root).delete()
    }

    // ── what is in the build, per mod ──────────────────────────────────────

    /**
     * Every plugin the current build was made from, by content.
     *
     * The stamp above answers "is anything different" for the whole folder,
     * which is the question a rebuild prompt needs. This answers "is THIS file
     * in the game you are about to play", which is the question somebody
     * looking at a list of six mods has -- and the two are not the same
     * question: a folder is stale the moment one mod is replaced, and the
     * other five are still built.
     *
     * By content, not by name, so that a mod updated in place is correctly no
     * longer the one that was compiled in.
     */
    private fun builtFile(root: File): File = File(root, "mods.built")

    private fun writeBuilt(mods: File, root: File) {
        try {
            val lines = all(mods).map { "${digest(it)}  ${relativePath(mods, it)}" }
            if (lines.isEmpty()) builtFile(root).delete()
            else builtFile(root).writeText(lines.joinToString("\n") + "\n")
        } catch (t: Throwable) {
            LauncherLog.log("mods: could not record what was built: $t")
        }
    }

    /** Digests of the plugins in the build, by the path the user sees. */
    fun built(root: File): Map<String, String> {
        val f = builtFile(root)
        if (!f.isFile) return emptyMap()
        return try {
            f.readLines().mapNotNull { line ->
                val parts = line.trim().split("  ", limit = 2)
                if (parts.size == 2 && parts[0].isNotEmpty()) parts[1] to parts[0] else null
            }.toMap()
        } catch (t: Throwable) {
            emptyMap()
        }
    }

    /**
     * Whether this exact file is in the build.
     *
     * Null means "cannot tell": a build made before this was recorded has no
     * list, and answering "no" for every mod in it would be a screen full of
     * red about a game that is working. The caller falls back to the stamp,
     * which is what that build was judged by.
     */
    fun isBuilt(mods: File, root: File, dll: File): Boolean? {
        val known = built(root)
        if (known.isEmpty()) return null
        return known[relativePath(mods, dll)] == digest(dll)
    }

    private fun digest(file: File): String {
        val sha = MessageDigest.getInstance("SHA-256")
        file.inputStream().use { sha.updateFrom(it) }
        return sha.digest().joinToString("") { "%02x".format(it) }
    }

    // ── the weaver ─────────────────────────────────────────────────────────

    private const val WEAVER_ASSET_DIR = "ondevice/mod-weaver"
    private const val WEAVER_DLL = "ModWeaver.dll"

    fun reportFile(root: File): File = File(root, "mods.report.json")

    /** One plugin, as the last weave found it. */
    data class Plugin(
        val file: String,
        val assembly: String,
        val guid: String,
        val name: String,
        val version: String,
        val status: String,
        val patched: Int,
        val issues: List<String>,
    ) {
        val ok: Boolean get() = status == "Ok"
        val failed: Boolean get() = status == "Failed"
        val title: String get() = if (name.isNotEmpty()) name else assembly.ifEmpty { file }
    }

    /** The last report, for the launcher to show without rebuilding. */
    fun lastReport(root: File): List<Plugin> {
        val f = reportFile(root)
        if (!f.isFile) return emptyList()
        return try {
            parse(f.readText())
        } catch (t: Throwable) {
            LauncherLog.log("mods: could not read the last report: $t")
            emptyList()
        }
    }

    private fun parse(text: String): List<Plugin> {
        val plugins = org.json.JSONObject(text).optJSONArray("plugins") ?: return emptyList()
        return (0 until plugins.length()).map { i ->
            val p = plugins.getJSONObject(i)
            val issues = p.optJSONArray("Issues")
            Plugin(
                file = p.optString("File"),
                assembly = p.optString("Assembly"),
                guid = p.optString("Guid"),
                name = p.optString("Name"),
                version = p.optString("Version"),
                status = p.optString("Status"),
                patched = p.optInt("Patched"),
                issues = (0 until (issues?.length() ?: 0)).map { issues!!.getString(it) },
            )
        }
    }

    /** Unpacks mod-weaver out of the APK, beside the build it operates on. */
    private fun stageWeaver(root: File, assets: android.content.res.AssetManager): File {
        val dir = File(root, "mod-weaver")
        dir.mkdirs()
        for (name in assets.list(WEAVER_ASSET_DIR).orEmpty()) {
            val out = File(dir, name)
            assets.open("$WEAVER_ASSET_DIR/$name").use { input ->
                out.outputStream().use { o -> input.copyTo(o) }
            }
        }
        val dll = File(dir, WEAVER_DLL)
        if (!dll.isFile) throw IOException("mod-weaver is not in the APK")
        return dll
    }

    /**
     * The port's own weaves, over the staged assemblies.
     *
     * Separate from [weave] and unconditional, because these are not the
     * user's mods: they ship with the port, there is no folder to look in, and
     * the overwhelmingly common build has no plugins at all and would skip
     * [weave] entirely. See the weaver's Builtin.cs for what they do.
     *
     * Never throws. A built-in weave that cannot be applied leaves the game
     * exactly as Team Cherry shipped it, which is a game that works; failing a
     * twenty-minute build over a frill would be the worse outcome by a wide
     * margin, and the setting that depends on it says so at runtime instead.
     */
    suspend fun weaveBuiltin(
        context: android.content.Context,
        root: File,
        assemblies: File,
        assets: android.content.res.AssetManager,
        onLine: (String) -> Unit = {},
    ) {
        try {
            val weaver = stageWeaver(root, assets)
            val argv = arrayListOf("builtin", "--assemblies", assemblies.absolutePath)
            val result = MonoRuntime.exec(
                context, weaver, argv, cwd = weaver.parentFile, onLine = onLine,
            )
            // Unconditionally, and that is the point: a weave that quietly did
            // nothing and a weave that quietly worked look identical from the
            // outside, and the setting that depends on this is the only thing
            // that would eventually notice. One line either way is the whole
            // difference between a fixable report and a mystery.
            val said = result.output.trim().lines().filter { it.isNotBlank() }
            if (said.isEmpty()) {
                LauncherLog.log("builtin weave: exit ${result.code}, no output")
            } else {
                for (line in said) LauncherLog.log("builtin weave: $line")
            }
            if (!result.ok) {
                LauncherLog.log("builtin weave: exit ${result.code}; stock behaviour kept")
            }
        } catch (t: Throwable) {
            LauncherLog.log("builtin weave: skipped", t)
        }
    }

    /**
     * Runs the chainloader over the staged assemblies.
     *
     * Every plugin in the folder, switched on or not: the gate the weaver
     * wraps each patch in is what decides that, and it is read at startup.
     *
     * Failures are reported, not thrown. One broken plugin out of six should
     * cost that plugin and nothing else -- and the whole point of doing this
     * before the native build is that a mod which cannot work says so now,
     * rather than after seventeen minutes of clang.
     */
    suspend fun weave(
        context: android.content.Context,
        root: File,
        mods: File,
        assemblies: File,
        assets: android.content.res.AssetManager,
        onLine: (String) -> Unit = {},
    ): List<Plugin> {
        val plugins = all(mods)
        if (plugins.isEmpty()) {
            reportFile(root).delete()
            writeGates(mods, root)
            return emptyList()
        }

        val weaver = stageWeaver(root, assets)
        val argv = ArrayList<String>()
        argv += "weave"
        argv += "--assemblies"
        argv += assemblies.absolutePath
        argv += "--report"
        argv += reportFile(root).absolutePath
        for (dll in plugins) {
            argv += "--mod"
            argv += dll.absolutePath
        }

        val result = MonoRuntime.exec(context, weaver, argv, cwd = weaver.parentFile, onLine = onLine)
        if (!result.ok) {
            throw IOException(
                "the mod weaver failed: " +
                    (result.output.trim().lines().lastOrNull() ?: "exit ${result.code}").take(300),
            )
        }

        val report = lastReport(root)
        for (p in report) {
            LauncherLog.log(
                "mod ${p.title}: ${p.status}, ${p.patched} patch(es)" +
                    if (p.issues.isEmpty()) "" else " -- ${p.issues.joinToString("; ")}",
            )
        }
        // Now that the report exists, file names can be turned into assembly
        // names, which is the only form the game can act on.
        writeGates(mods, root)
        return report
    }

    /**
     * Assemblies that came from the mods folder and are now in the build.
     *
     * Named by assembly rather than by file, because that is what the player
     * resolves by, and it is what the weaver staged them under.
     */
    fun stagedAssemblies(root: File): List<String> =
        lastReport(root).filterNot { it.failed }.map { it.assembly }.filter { it.isNotEmpty() }
}
