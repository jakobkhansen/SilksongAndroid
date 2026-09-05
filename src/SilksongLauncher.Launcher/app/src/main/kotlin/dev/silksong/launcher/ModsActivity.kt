// ModsActivity — what is installed, what worked, and what to do about it.
//
// The screen exists because of one property of this design: mods are compiled
// into the game rather than loaded by it, so a mod that cannot work fails at
// BUILD time. That is a real advantage over a runtime loader -- the failure is
// a line of text before anything is built, rather than a crash twenty minutes
// later on a handheld with no console -- but only if somebody is shown it.
//
// So each plugin is listed with what the weaver made of it: how many patches
// it applied, and every one it could not. The toggle takes effect the next
// time the game starts and costs nothing -- every plugin here is already
// compiled in, and the switch only decides whether its gate is opened. Adding
// or removing a file is the change that needs a rebuild, and the screen says
// so rather than letting the next launch quietly be the old build.

package dev.silksong.launcher

import android.app.Activity
import android.content.Intent
import android.graphics.Color
import android.graphics.Typeface
import android.os.Bundle
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.widget.Button
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.Switch
import android.widget.TextView
import android.widget.Toast
import java.io.File

class ModsActivity : Activity() {

    private companion object {
        const val PICK_FOLDER = 41
    }

    private lateinit var list: LinearLayout
    private lateinit var mods: File

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        mods = Mods.dir(this)
        runCatching { Mods.ensure(mods) }
        setContentView(buildUi())
        populate()
    }

    override fun onResume() {
        super.onResume()
        // The folder is edited from outside this app -- over USB, or in a file
        // manager -- so what was on screen a minute ago is not evidence of
        // anything.
        populate()
    }

    private fun buildUi(): View {
        val root = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setBackgroundColor(Color.parseColor("#0D0A0B"))
            setPadding(dp(20), dp(20), dp(20), dp(20))
        }

        root.addView(TextView(this).apply {
            text = "Mods"
            setTextColor(Color.WHITE)
            textSize = 22f
            setTypeface(typeface, Typeface.BOLD)
        })

        root.addView(TextView(this).apply {
            text = "BepInEx 5 plugins, compiled into the game. Install one below, or put " +
                "DLLs in\n" + mods.absolutePath +
                "\nAdding, removing or disabling one takes effect on the next build."
            setTextColor(Color.parseColor("#7A6E71"))
            textSize = 12f
            setPadding(0, dp(4), 0, dp(12))
        })

        // The default way in. A mod arrives as a folder -- extracted from a
        // download, on the device it was downloaded to -- and the alternative
        // is asking somebody to find Android/data in a file manager that may
        // not show it.
        root.addView(
            button("Install a mod from a folder", primary = true) { pick() },
            LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT)
                .apply { bottomMargin = dp(12) },
        )

        val scroll = ScrollView(this).apply { isFillViewport = true }
        list = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        scroll.addView(list)
        root.addView(
            scroll,
            LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, 0, 1f),
        )

        val buttons = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.END
            setPadding(0, dp(12), 0, 0)
        }
        buttons.addView(
            button("Rebuild", primary = true) { rebuild() },
            LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f).apply {
                marginEnd = dp(10)
            },
        )
        buttons.addView(
            button("Close") { finish() },
            LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f),
        )
        root.addView(buttons)

        return root
    }

    private fun populate() {
        list.removeAllViews()

        val found = Mods.all(mods)
        if (found.isEmpty()) {
            list.addView(note("Nothing installed yet. Install a mod from a folder, above."))
            list.addView(
                note(
                    "Transpilers, patch targets chosen at runtime and Reflection.Emit " +
                        "cannot work here; everything else generally does.",
                ),
            )
            return
        }

        val off = Mods.disabled(mods)
        val root = Il2cppConverter.rootFor(this)
        // Keyed by file name, because that is what the report carries and it
        // is not always what the assembly inside is called.
        val reports = Mods.lastReport(root).associateBy { it.file }
        val stale = Mods.isStale(mods, root, assets)

        if (stale) {
            list.addView(
                note(
                    "A mod has been added, replaced or removed since the last build. " +
                        "Rebuild to apply it. Switching one on or off does not need one.",
                ),
            )
        }

        for (dll in found) {
            val relative = Mods.relativePath(mods, dll)
            // Per mod, not per folder: one replaced plugin makes the folder
            // stale while the others are still in the game, and a list that
            // called all six unbuilt would be lying about five of them.
            val built = Mods.isBuilt(mods, root, dll) ?: !stale
            list.addView(row(relative, reports[dll.name], relative !in off, built))
        }
    }

    private fun row(relative: String, report: Mods.Plugin?, enabled: Boolean, built: Boolean): View {
        val card = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(12), dp(10), dp(12), dp(10))
            setBackgroundColor(Color.parseColor("#161112"))
        }

        val header = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
        }
        header.addView(
            TextView(this).apply {
                text = report?.title ?: relative
                setTextColor(Color.WHITE)
                textSize = 15f
            },
            LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f),
        )
        // Whether this file is in the game as it stands. The dot carries the
        // answer and the word says what the dot means, because a colour on its
        // own is no answer to somebody who cannot tell these two apart.
        header.addView(
            TextView(this).apply {
                text = if (built) "● built" else "● not built"
                setTextColor(Color.parseColor(if (built) "#6FCF7A" else "#C88A94"))
                textSize = 11f
                setPadding(0, 0, dp(8), 0)
            },
        )
        @Suppress("DEPRECATION")
        header.addView(
            Switch(this).apply {
                isChecked = enabled
                setOnCheckedChangeListener { _, checked ->
                    Mods.setEnabled(mods, Il2cppConverter.rootFor(this@ModsActivity), relative, checked)
                    LauncherLog.log("mods: $relative ${if (checked) "enabled" else "disabled"}")
                    populate()
                }
            },
        )
        card.addView(header)

        card.addView(
            TextView(this).apply {
                text = summary(relative, report, enabled)
                setTextColor(Color.parseColor("#7A6E71"))
                textSize = 11f
            },
        )

        for (issue in report?.issues.orEmpty()) {
            card.addView(
                TextView(this).apply {
                    text = "• $issue"
                    setTextColor(Color.parseColor("#C88A94"))
                    textSize = 11f
                    setPadding(0, dp(2), 0, 0)
                },
            )
        }

        val wrapper = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(0, 0, 0, dp(10))
        }
        wrapper.addView(card, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT))
        return wrapper
    }

    private fun summary(relative: String, report: Mods.Plugin?, enabled: Boolean): String {
        if (!enabled) return "$relative — off"
        if (report == null) return "$relative — not built yet"
        val version = if (report.version.isEmpty()) "" else " v${report.version}"
        val patches = if (report.patched == 1) "1 patch" else "${report.patched} patches"
        return when (report.status) {
            "Ok" -> "$relative$version — $patches applied"
            "Partial" -> "$relative$version — $patches applied, with problems"
            else -> "$relative$version — not built"
        }
    }

    private fun note(text: String) = TextView(this).apply {
        this.text = text
        setTextColor(Color.parseColor("#7A6E71"))
        textSize = 12f
        setPadding(0, 0, 0, dp(10))
    }

    /**
     * Back to the build screen, which already knows how to do the least work
     * that would apply the change.
     */
    private fun rebuild() {
        try {
            startActivity(
                Intent(this, SetupActivity::class.java)
                    .putExtra(SetupActivity.EXTRA_REBUILD, true),
            )
            finish()
        } catch (t: Throwable) {
            Toast.makeText(this, "Could not open the build screen: ${t.message}", Toast.LENGTH_LONG).show()
        }
    }

    // ── installing one ─────────────────────────────────────────────────────

    private fun pick() {
        try {
            startActivityForResult(ModImport.intent(), PICK_FOLDER)
        } catch (t: Throwable) {
            Toast.makeText(this, "No folder picker on this device: ${t.message}", Toast.LENGTH_LONG).show()
        }
    }

    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        super.onActivityResult(requestCode, resultCode, data)
        if (requestCode != PICK_FOLDER) return
        val uri = data?.data
        if (resultCode != RESULT_OK || uri == null) return

        // On the main thread on purpose: a mod is a handful of files, the
        // screen it lands on is a list that has to be right immediately
        // afterwards, and a copy that took long enough to need a thread is one
        // the size guard should have refused.
        val result = try {
            ModImport.copy(this, uri, mods)
        } catch (t: Throwable) {
            LauncherLog.log("mods: import failed: $t")
            android.app.AlertDialog.Builder(this)
                .setTitle("That folder could not be installed")
                .setMessage(t.message ?: t.toString())
                .setPositiveButton("OK", null)
                .show()
            return
        }

        populate()
        offerRebuild(result)
    }

    /**
     * The offer, not the decision -- as on the launch path. A rebuild is
     * several minutes, and somebody installing three mods in a row wants to be
     * asked once at the end rather than three times.
     */
    private fun offerRebuild(result: ModImport.Result) {
        val what = if (result.plugins == 1) "1 assembly" else "${result.plugins} assemblies"
        android.app.AlertDialog.Builder(this)
            .setTitle("Installed ${result.name}")
            .setMessage(
                "$what copied in.\n\n" +
                    "Mods are compiled into the game, so it has to be built again before this " +
                    "one does anything. That takes a few minutes; only what changed is redone.\n\n" +
                    "Rebuild now, or carry on and do it later?",
            )
            .setPositiveButton("Rebuild now") { _, _ -> rebuild() }
            .setNegativeButton("Later", null)
            .show()
    }

    private fun button(label: String, primary: Boolean = false, onClick: () -> Unit) =
        Button(this).apply {
            text = label
            setOnClickListener { onClick() }
            backgroundTintList = android.content.res.ColorStateList.valueOf(
                Color.parseColor(if (primary) "#7D3341" else "#B4AEB2"),
            )
            setTextColor(if (primary) Color.WHITE else Color.parseColor("#0D0A0B"))
            setPadding(paddingLeft, dp(12), paddingRight, dp(12))
        }

    private fun dp(v: Int) = (v * resources.displayMetrics.density).toInt()
}
