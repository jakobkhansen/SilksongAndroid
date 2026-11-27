package dev.silksong.launcher

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import java.io.File
import java.io.IOException

class BuildInstallationTest {
    @get:Rule val temp = TemporaryFolder()
    private lateinit var pkg: File
    private lateinit var mods: File
    private lateinit var out: File
    private val signature = "2|test-assets"
    private val engine: File get() = File(pkg, "lib/arm64/libil2cpp.so")
    private val image: File get() = File(pkg, "data.apk")

    @Before fun setUp() {
        pkg = temp.newFolder("pkg")
        mods = temp.newFolder("mods")
        out = temp.newFolder("build")
        engine.parentFile!!.mkdirs()
    }

    private fun plugin(name: String = "Plugin.dll", contents: String = "old"): File =
        File(mods, name).apply { parentFile!!.mkdirs(); writeText(contents) }

    private fun report(vararg entries: Pair<File, String>): List<Mods.Plugin> =
        entries.map { (file, status) ->
            Mods.Plugin(file.name, file.nameWithoutExtension, "", "", "", status, 0, emptyList())
        }

    private fun installInitial(plugin: File) {
        engine.writeText("old engine")
        image.writeText("old metadata")
        Mods.markConverted(out, Mods.snapshot(mods), report(plugin to "Ok"))
        Mods.markInstalled(out)
        BuildInstallation.complete(pkg, signature)
    }

    @Test fun incompleteRebuildNeverBecomesLaunchableAtAnIntermediateCheckpoint() {
        val plugin = plugin()
        installInitial(plugin)
        val previouslyBuilt = Mods.built(out)
        assertTrue(BuildInstallation.isReady(pkg, signature))
        assertFalse(Mods.isStale(mods, out))

        plugin.writeText("new")
        assertTrue(Mods.isStale(mods, out))
        assertTrue(Mods.isConversionStale(mods, out))
        BuildInstallation.invalidate(pkg)
        assertFalse(BuildInstallation.isReady(pkg, signature))

        Mods.markConverted(out, Mods.snapshot(mods), report(plugin to "Ok"))
        assertFalse(Mods.isConversionStale(mods, out))
        assertTrue(Mods.isStale(mods, out))
        assertEquals(previouslyBuilt, Mods.built(out))
        assertFalse(BuildInstallation.isReady(pkg, signature))

        engine.writeText("new engine")
        assertEquals("old metadata", image.readText())
        assertFalse(BuildInstallation.isReady(pkg, signature))
        image.writeText("new metadata")
        assertFalse(BuildInstallation.isReady(pkg, signature))

        Mods.markInstalled(out)
        assertFalse(Mods.isStale(mods, out))
        assertFalse(BuildInstallation.isReady(pkg, signature))
        BuildInstallation.complete(pkg, signature)
        assertTrue(BuildInstallation.isReady(pkg, signature))
        assertEquals(true, Mods.isBuilt(mods, out, plugin))
    }

    @Test fun failedConversionKeepsInstalledModHistoryButCannotLeaveAReadyMarker() {
        val plugin = plugin()
        installInitial(plugin)
        val previouslyBuilt = Mods.built(out)
        BuildInstallation.invalidate(pkg)
        plugin.writeText("replacement")
        Mods.clearStamp(out)
        assertTrue(Mods.isConversionStale(mods, out))
        assertTrue(Mods.isStale(mods, out))
        assertEquals(previouslyBuilt, Mods.built(out))
        assertEquals("old engine", engine.readText())
        assertEquals("old metadata", image.readText())
        assertFalse(BuildInstallation.isReady(pkg, signature))
    }

    @Test fun installationUsesCapturedInputsInsteadOfRehashingTheLiveFolder() {
        val plugin = plugin()
        val converted = Mods.snapshot(mods)
        Mods.markConverted(out, converted, report(plugin to "Ok"))
        plugin.writeText("changed while compiling native code")
        engine.writeText("engine from captured input")
        image.writeText("metadata from captured input")
        Mods.markInstalled(out)
        BuildInstallation.complete(pkg, signature)
        assertTrue(BuildInstallation.isReady(pkg, signature))
        assertEquals(converted.files, Mods.built(out))
        assertTrue(Mods.isStale(mods, out))
        assertTrue(Mods.isConversionStale(mods, out))
        assertEquals(false, Mods.isBuilt(mods, out, plugin))
    }

    @Test fun togglesAndConfigChangesDoNotRequireAnotherBuild() {
        val plugin = plugin()
        installInitial(plugin)
        File(mods, "disabled.txt").writeText(plugin.name)
        File(mods, "disabled-assemblies.txt").writeText(plugin.nameWithoutExtension)
        File(File(mods, "config").apply { mkdirs() }, "Plugin.cfg").writeText("changed settings")
        assertFalse(Mods.isStale(mods, out))
        assertFalse(Mods.isConversionStale(mods, out))
        assertEquals(true, Mods.isBuilt(mods, out, plugin))
        assertTrue(BuildInstallation.isReady(pkg, signature))
    }

    @Test fun rejectedPluginsAreProcessedButNotMarkedBuilt() {
        val good = plugin("Good.dll")
        val partial = plugin("Partial.dll")
        val bad = plugin("Bad.dll")
        Mods.markConverted(
            out, Mods.snapshot(mods),
            report(good to "Ok", partial to "Partial", bad to "Failed"),
        )
        Mods.markInstalled(out)
        assertFalse(Mods.isStale(mods, out))
        assertFalse(Mods.isConversionStale(mods, out))
        assertEquals(true, Mods.isBuilt(mods, out, good))
        assertEquals(true, Mods.isBuilt(mods, out, partial))
        assertEquals(false, Mods.isBuilt(mods, out, bad))

        Mods.markConverted(
            out, Mods.snapshot(mods),
            report(good to "Failed", partial to "Failed", bad to "Failed"),
        )
        Mods.markInstalled(out)
        assertFalse(Mods.isStale(mods, out))
        assertTrue(Mods.built(out).isEmpty())
        assertEquals(false, Mods.isBuilt(mods, out, good))
    }

    @Test fun removingTheLastModRecordsAnEmptyInstalledSet() {
        val plugin = plugin()
        installInitial(plugin)
        assertTrue(plugin.delete())
        assertTrue(Mods.isStale(mods, out))
        Mods.markConverted(out, Mods.snapshot(mods), emptyList())
        assertTrue(Mods.isStale(mods, out))
        Mods.markInstalled(out)
        assertFalse(Mods.isStale(mods, out))
        assertFalse(Mods.isConversionStale(mods, out))
        assertTrue(Mods.built(out).isEmpty())
    }

    @Test fun legacyMarkersAndMissingConversionSnapshotsAreNotTrusted() {
        plugin()
        engine.writeText("engine")
        image.writeText("metadata")
        File(pkg, ".built").writeText("1|test-assets")
        File(out, "mods.stamp").writeText(Mods.stamp(mods))
        assertFalse(BuildInstallation.isReady(pkg, signature))
        assertTrue(Mods.isConversionStale(mods, out))
        assertTrue(Mods.isStale(mods, out))
    }

    @Test fun missingOrEmptyOutputsCannotBeCommitted() {
        assertThrows(IOException::class.java) { BuildInstallation.complete(pkg, signature) }
        engine.writeText("engine")
        image.writeText("")
        assertThrows(IOException::class.java) { BuildInstallation.complete(pkg, signature) }
        image.delete()
        image.mkdir()
        assertThrows(IOException::class.java) { BuildInstallation.complete(pkg, signature) }
        assertFalse(File(pkg, ".built").exists())
    }

    @Test fun failedInvalidationIsAnErrorRatherThanPermissionToContinue() {
        val marker = File(pkg, ".built").apply { mkdirs() }
        File(marker, "keep").writeText("not a completion marker")
        assertThrows(IOException::class.java) { BuildInstallation.invalidate(pkg) }
        assertTrue(File(marker, "keep").isFile)
    }

    @Test fun missingOrCorruptConvertedRecordsCannotBeInstalled() {
        assertThrows(IOException::class.java) { Mods.markInstalled(out) }
        File(out, "mods.converted").writeText("not a digest list")
        File(out, "mods.stamp").writeText(Mods.stamp(mods))
        assertThrows(IOException::class.java) { Mods.markInstalled(out) }
        assertFalse(File(out, "mods.installed.stamp").exists())
    }

    @Test fun unityPiecesFromAnotherEngineAreNotPresent() {
        val unity = temp.newFolder("unity")
        for (path in listOf(
            "editor/Editor/Data/il2cpp/build/deploy/il2cpp.dll",
            "packages/com.unity.inputsystem/InputSystem/Unity.InputSystem.asmdef",
            "android/Variations/il2cpp/Release/Libs/arm64-v8a/libunity.so",
        )) File(unity, path).apply { parentFile!!.mkdirs(); writeText("piece") }

        // Every download made before the stamp existed was 6000.0.50f1.
        assertFalse(UnityFetcher.isPresent(unity))
        File(unity, "engine").writeText("6000.0.50f1 (f1ef1dca8bff)")
        assertFalse(UnityFetcher.isPresent(unity))

        UnityFetcher.markCurrent(unity)
        assertTrue(UnityFetcher.isPresent(unity))
    }
}
