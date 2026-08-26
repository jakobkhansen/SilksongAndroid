// LogActivity — the log, on screen, in a form that can be sent to someone.
//
// This exists because of the bugs that only happen on hardware nobody working
// on the port owns. The launcher already kept a log, but it was memory in one
// process and a panel on one screen: by the time a user was asked for it, the
// app had been restarted and it was gone, and even when it was there it could
// not be got out of the phone.
//
// So: everything on disk (LauncherLog.attach), every session, and three ways
// out of the device. Share is first because it is the one that reaches another
// person -- mail, chat, an issue -- without anybody having to explain what a
// clipboard is on a handheld with no keyboard. Save is second because it is
// the one that works when the log is too big to hand to another app through
// an Intent, and because a file in Downloads can be attached to anything.
//
// Built in code rather than XML for the same reason SetupActivity is: it is one
// screen of four buttons and a scroller, and keeping it here means the whole
// thing is readable in one place.

package dev.silksong.launcher

import android.app.Activity
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
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
import android.widget.TextView
import android.widget.Toast
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

class LogActivity : Activity() {

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)

    private lateinit var text: TextView

    /** The whole report, once it has been read. Save writes this; the screen shows its tail. */
    private var full: String = ""

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(buildUi())

        // Off the main thread, and not only out of tidiness: the log is a
        // quarter of a megabyte read from a filesystem that may be busy with
        // the very build being complained about.
        text.text = "Reading the log…"
        scope.launch {
            val report = withContext(Dispatchers.IO) { report() }
            full = report
            show(report)
        }
    }

    override fun onDestroy() {
        scope.cancel()
        super.onDestroy()
    }

    /**
     * Puts the log on screen, at a size a TextView can survive.
     *
     * This screen used to hand the entire file to one selectable TextView and
     * it is what brought the app down on other people's phones. A TextView has
     * no recycling: before the first frame it builds a StaticLayout over every
     * character it was given, measuring each of thousands of lines, and
     * setTextIsSelectable puts that behind a SPANNABLE buffer and an Editor as
     * well. On a log of any size that is seconds of main thread and tens of
     * megabytes of allocation -- an ANR on a good device and an OOM on a
     * handheld, both of them landing on the person who was already having a
     * problem and had gone looking for the log to report it.
     *
     * So the screen shows the end, which is the part that says what went
     * wrong, and Save is what produces the rest.
     */
    private fun show(report: String) {
        val shown = tail(report, MAX_ON_SCREEN)
        // Selection is what makes a partial copy possible by hand, and it is
        // also the single most expensive thing this view can do, so it is
        // switched on only where it is cheap.
        text.setTextIsSelectable(shown.length <= MAX_SELECTABLE)
        text.text = if (shown.length < report.length) {
            "… showing the last ${shown.length / 1024} KB of ${report.length / 1024} KB. " +
                "Save writes the whole log to a file.\n\n$shown"
        } else {
            shown
        }
    }

    /** The last [max] characters, cut at a line boundary so it does not start mid-word. */
    private fun tail(s: String, max: Int): String {
        if (s.length <= max) return s
        val cut = s.length - max
        val nl = s.indexOf('\n', cut)
        return s.substring(if (nl >= 0) nl + 1 else cut)
    }

    /**
     * What actually gets sent.
     *
     * The device line goes first and is repeated here even though attach()
     * already wrote one per session: a log trimmed back to fit its cap can
     * lose the header, and a report whose first question is "which phone?" is
     * a round trip nobody needs.
     */
    private fun report(): String {
        val history = LauncherLog.history(this)
        val body = if (history.isNotBlank()) history else LauncherLog.snapshot().joinToString("\n")
        return buildString {
            append("SilksongAndroid ").append(appVersion()).append('\n')
            append(LauncherLog.deviceSummary()).append('\n')
            append('\n')
            append(if (body.isBlank()) "(no log yet)" else body)
        }
    }

    /** Read from the package rather than a constant, so it cannot disagree with the APK. */
    private fun appVersion(): String =
        try {
            packageManager.getPackageInfo(packageName, 0).versionName ?: "?"
        } catch (t: Throwable) {
            "?"
        }

    private fun buildUi(): View {
        val root = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setBackgroundColor(Color.parseColor("#0D0A0B"))
            setPadding(dp(20), dp(20), dp(20), dp(20))
        }

        root.addView(TextView(this).apply {
            text = "Logs"
            setTextColor(Color.WHITE)
            textSize = 22f
            setTypeface(typeface, Typeface.BOLD)
        })

        root.addView(TextView(this).apply {
            text = "Everything the launcher has recorded, including previous runs. " +
                "Send this when reporting a problem."
            setTextColor(Color.parseColor("#7A6E71"))
            textSize = 12f
            setPadding(0, dp(4), 0, dp(4))
        })

        // The path, on screen, because the question this screen exists to
        // answer is sometimes asked of a device that cannot get to this screen
        // at all -- and because "it is a file, here" is a shorter conversation
        // than any of the buttons below.
        LauncherLog.externalFile(this)?.let { f ->
            root.addView(TextView(this).apply {
                text = "Also a file at ${f.absolutePath}"
                setTextColor(Color.parseColor("#5C5254"))
                textSize = 10f
                typeface = Typeface.MONOSPACE
                setPadding(0, 0, 0, dp(12))
            })
        }

        // Weight 1 so the scroller takes the space the buttons do not, rather
        // than the buttons being pushed off a long log.
        val scroll = ScrollView(this).apply { isFillViewport = true }
        text = TextView(this).apply {
            setTextColor(Color.parseColor("#B5A9AC"))
            textSize = 11f
            typeface = Typeface.MONOSPACE
        }
        scroll.addView(text)
        root.addView(scroll, LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT, 0, 1f))

        val buttons = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.END
            setPadding(0, dp(12), 0, 0)
        }
        buttons.addView(button("Share", primary = true) { share() },
            LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f).apply {
                marginEnd = dp(8)
            })
        buttons.addView(button("Save") { save() },
            LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f).apply {
                marginEnd = dp(8)
            })
        buttons.addView(button("Copy") { copy() },
            LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f).apply {
                marginEnd = dp(8)
            })
        buttons.addView(button("Close") { finish() },
            LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
        root.addView(buttons)

        return root
    }

    private fun button(label: String, primary: Boolean = false, onClick: () -> Unit) =
        Button(this).apply {
            text = label
            setOnClickListener { onClick() }
            backgroundTintList = android.content.res.ColorStateList.valueOf(
                Color.parseColor(if (primary) "#7D3341" else "#B4AEB2"))
            setTextColor(if (primary) Color.WHITE else Color.parseColor("#0D0A0B"))
            setPadding(paddingLeft, dp(12), paddingRight, dp(12))
        }

    /**
     * The most of the log that will fit through an Intent or the clipboard.
     *
     * Both cross a Binder, and a Binder transaction buffer is one megabyte
     * PER PROCESS, shared with every other transaction in flight -- so a big
     * enough log does not produce a polite failure, it produces
     * TransactionTooLargeException from somewhere unrelated. The tail is the
     * part worth keeping: a failure explains itself at the end.
     */
    private fun sendable(): String = tail(full.ifBlank { report() }, MAX_SENDABLE)

    private fun share() {
        val body = sendable()
        val intent = Intent(Intent.ACTION_SEND).apply {
            type = "text/plain"
            putExtra(Intent.EXTRA_SUBJECT, "SilksongAndroid log")
            putExtra(Intent.EXTRA_TEXT, body)
        }
        try {
            startActivity(Intent.createChooser(intent, "Send the log"))
        } catch (t: Throwable) {
            // A device with nothing that accepts text is unusual but not
            // impossible, and falling back beats an unexplained dead button.
            copy()
        }
    }

    private fun copy() {
        try {
            val clipboard = getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
            clipboard.setPrimaryClip(ClipData.newPlainText("SilksongAndroid log", sendable()))
            Toast.makeText(this, "Log copied", Toast.LENGTH_SHORT).show()
        } catch (t: Throwable) {
            Toast.makeText(this, "Could not copy: ${t.message}", Toast.LENGTH_LONG).show()
        }
    }

    /**
     * Writes the whole log wherever the user says.
     *
     * The one route out with no size limit and no permission: the picker hands
     * back a URI that is already granted, and it lands somewhere a file manager
     * can see -- unlike either of the directories the log is actually kept in,
     * one of which is unreachable on a stock device and the other of which
     * Android 11 stopped letting file managers browse.
     */
    private fun save() {
        val stamp = SimpleDateFormat("yyyyMMdd-HHmmss", Locale.US).format(Date())
        val intent = Intent(Intent.ACTION_CREATE_DOCUMENT).apply {
            addCategory(Intent.CATEGORY_OPENABLE)
            type = "text/plain"
            putExtra(Intent.EXTRA_TITLE, "silksong-log-$stamp.txt")
        }
        try {
            startActivityForResult(intent, REQ_SAVE)
        } catch (t: Throwable) {
            Toast.makeText(this, "No file picker on this device: ${t.message}", Toast.LENGTH_LONG).show()
        }
    }

    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        super.onActivityResult(requestCode, resultCode, data)
        if (requestCode != REQ_SAVE || resultCode != RESULT_OK) return
        val uri = data?.data ?: return
        scope.launch {
            val error = withContext(Dispatchers.IO) {
                try {
                    // Read again if Save was pressed before the load finished,
                    // rather than silently writing an empty file.
                    val body = full.ifBlank { report() }
                    contentResolver.openOutputStream(uri)?.use { it.write(body.toByteArray()) }
                        ?: return@withContext "the picker returned nothing to write to"
                    null
                } catch (t: Throwable) {
                    t.message ?: t.toString()
                }
            }
            Toast.makeText(
                this@LogActivity,
                if (error == null) "Log saved" else "Could not save: $error",
                if (error == null) Toast.LENGTH_SHORT else Toast.LENGTH_LONG,
            ).show()
        }
    }

    private fun dp(v: Int) = (v * resources.displayMetrics.density).toInt()

    private companion object {
        /** Characters handed to the TextView. See [show]. */
        const val MAX_ON_SCREEN = 64 * 1024

        /** Below this, selection is cheap enough to leave on. */
        const val MAX_SELECTABLE = 16 * 1024

        /** Characters that may cross a Binder. See [sendable]. */
        const val MAX_SENDABLE = 128 * 1024

        const val REQ_SAVE = 41
    }
}
