// LauncherActivity — the menu, and the app's entry point once the game has
// been built. GameActivity is a regular non-launchable Activity, invoked only
// via the Intent in launchGame().
//
// One panel: a button stack (Log in, Pull saves, Push saves, Settings, Launch
// game) beside a live mirror of LauncherLog, so the user can see what is
// happening end to end. Login state is persisted via TokenStore.

package dev.silksong.launcher

import android.app.Activity
import android.content.Intent
import android.os.Bundle
import android.view.View
import android.view.WindowManager
import android.widget.Button
import android.widget.LinearLayout
import android.widget.ProgressBar
import android.widget.ScrollView
import android.widget.TextView
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch

class LauncherActivity : Activity() {

    private companion object {
        // The game is the depot-built player in this same package. It is not
        // Unity's own activity: dev.silksong.shell.GameActivity owns the
        // window and points the engine's library lookup at app storage.
        private const val UNITY_ACTIVITY_CLASS = "dev.silksong.shell.GameActivity"

        private const val REQ_LOGIN = 1
    }

    private val uiScope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)

    // Panels.
    private lateinit var launchPanel: LinearLayout

    // Launch-panel widgets.
    private lateinit var txtLoginStatus: TextView
    private lateinit var btnLogin: Button
    private lateinit var btnPull: Button
    private lateinit var btnPush: Button
    private lateinit var spinPull: ProgressBar
    private lateinit var spinPush: ProgressBar
    private lateinit var btnMods: Button
    private lateinit var btnSettings: Button
    private lateinit var btnLogs: Button
    private lateinit var btnLaunch: Button
    private lateinit var logScroll: ScrollView
    private lateinit var txtLog: TextView

    private lateinit var tokenStore: TokenStore
    private lateinit var settings: SettingsStore
    private var creds: TokenStore.Credentials? = null

    // True between launchGame() and the next onResume. Drives the
    // auto-push trigger: only push when we just returned from the game
    // (so a casual "user opened the launcher to read the log" doesn't
    // fire a push).
    private var returningFromGame: Boolean = false

    // Reentrancy guard for cloud operations — manual + auto can both
    // schedule pulls/pushes and we don't want overlapping sessions on
    // the same Steam account.
    private var cloudJob: Job? = null

    // Mirrors LauncherLog into the on-screen log panel.
    private val logListener = LauncherLog.Listener { _, snapshot ->
        runOnUiThread {
            txtLog.text = snapshot.joinToString("\n")
            logScroll.post { logScroll.fullScroll(View.FOCUS_DOWN) }
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        setContentView(R.layout.activity_launcher)

        launchPanel = findViewById(R.id.launch_panel)
        txtLoginStatus = findViewById(R.id.txt_login_status)
        btnLogin = findViewById(R.id.btn_login)
        btnPull = findViewById(R.id.btn_pull)
        btnPush = findViewById(R.id.btn_push)
        spinPull = findViewById(R.id.spin_pull)
        spinPush = findViewById(R.id.spin_push)
        btnMods = findViewById(R.id.btn_mods)
        btnSettings = findViewById(R.id.btn_settings)
        btnLogs = findViewById(R.id.btn_logs)
        btnLaunch = findViewById(R.id.btn_launch)
        logScroll = findViewById(R.id.log_scroll)
        txtLog = findViewById(R.id.txt_log)

        // Seed log mirror with any messages emitted before we attached.
        txtLog.text = LauncherLog.snapshot().joinToString("\n")
        LauncherLog.addListener(logListener)

        tokenStore = TokenStore(this)
        settings = SettingsStore(this)
        creds = tokenStore.read()
        refreshLoginUi()
        LauncherLog.log("Launcher ready. Logged in: ${creds != null}")

        // Attach click listeners up front — independent of which panel
        // is visible. setOnClickListener registers on the View object
        // itself; visibility transitions don't unregister anything.
        // Doing it here (vs deferring into showLaunchPanel's post()) is
        // important because the Ayn Thor's built-in gamepad input puts
        // Android into "non-touch focus mode": tap-1 just moves focus,
        // tap-2 fires the click — which was the actual two-press bug.
        // Setting listeners early + NEVER calling requestFocus() keeps
        // the activity in touch mode where tap = click.
        btnLogin.setOnClickListener { onLoginClicked() }
        btnPull.setOnClickListener { onPullClicked() }
        btnPush.setOnClickListener { onPushClicked() }
        btnMods.setOnClickListener {
            startActivity(Intent(this, ModsActivity::class.java))
        }
        btnSettings.setOnClickListener { onSettingsClicked() }
        btnLogs.setOnClickListener {
            startActivity(Intent(this, LogActivity::class.java))
        }
        btnLaunch.setOnClickListener { onLaunchClicked() }

        showLaunchPanel()
    }

    override fun onDestroy() {
        LauncherLog.removeListener(logListener)
        uiScope.cancel()
        super.onDestroy()
    }

    // ── State transitions ──────────────────────────────────────────────

    private fun showLaunchPanel() {
        launchPanel.visibility = View.VISIBLE
    }

    override fun onResume() {
        super.onResume()
        // Auto-push fires after the user returns from playing the
        // game. We use a flag (set in launchGame) instead of just
        // "always on resume" so dismissing dialogs / opening
        // Settings + coming back doesn't trigger a push.
        if (returningFromGame) {
            returningFromGame = false
            maybeAutoPush()
        }
    }

    // ── Login ──────────────────────────────────────────────────────────

    private fun onLoginClicked() {
        if (creds != null) {
            // Logged in already — this button doubles as "log out".
            LauncherLog.log("Logged out")
            tokenStore.clear()
            creds = null
            refreshLoginUi()
            return
        }
        @Suppress("DEPRECATION")
        startActivityForResult(Intent(this, LoginActivity::class.java), REQ_LOGIN)
    }

    @Deprecated("Use the Activity Result APIs — fine for Phase 1 scaffolding")
    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        super.onActivityResult(requestCode, resultCode, data)
        if (requestCode != REQ_LOGIN) return
        if (resultCode != RESULT_OK || data == null) {
            LauncherLog.log("Login cancelled")
            return
        }
        val account = data.getStringExtra(LoginActivity.EXTRA_ACCOUNT)
        val token = data.getStringExtra(LoginActivity.EXTRA_TOKEN)
        if (account.isNullOrEmpty() || token.isNullOrEmpty()) {
            LauncherLog.log("Login returned without account/token")
            return
        }
        val newCreds = TokenStore.Credentials(account, token)
        tokenStore.write(newCreds)
        creds = newCreds
        LauncherLog.log("Steam credentials saved")
        refreshLoginUi()
    }

    private fun refreshLoginUi() {
        val c = creds
        if (c == null) {
            txtLoginStatus.text = ""
            btnLogin.text = getString(R.string.action_log_in)
            btnPull.isEnabled = false
            btnPush.isEnabled = false
        } else {
            txtLoginStatus.text = "Signed in as ${c.accountName}"
            btnLogin.text = getString(R.string.action_log_in_as)
            btnPull.isEnabled = true
            btnPush.isEnabled = true
        }
    }

    // ── Cloud save: pull (Phase 1c) ────────────────────────────────────

    private fun onPullClicked() {
        val c = creds
        if (c == null) {
            LauncherLog.log("Pull: not logged in")
            return
        }
        runCloudJob(spinPull, btnPull, R.string.action_pull_saves_busy) { pullFlow(c, source = "manual") }
    }

    /**
     * Shared analyze + conflict-prompt + download pipeline used by the
     * Pull button and the pre-launch sync. Either pulls EVERYTHING the
     * analyzer flagged (safe set + conflicts when the user keeps remote)
     * or pulls NOTHING — never a per-slot mix.
     *
     * Returns true if the caller may proceed (synced, nothing to do, or
     * the conflict was resolved by keeping local/remote); false only
     * when the user cancelled an unresolved conflict — the pre-launch
     * path uses that to abort the game launch.
     */
    private suspend fun pullFlow(c: TokenStore.Credentials, source: String): Boolean {
        try {
            LauncherLog.log("Analyzing cloud vs local saves ($source pull)…")
            val analysis = CloudSync.analyzePull(this@LauncherActivity, c)

            if (analysis.toDownload.isEmpty() && analysis.conflicts.isEmpty()) {
                LauncherLog.log("Pull: nothing newer on cloud")
                return true
            }

            if (analysis.hasConflicts) {
                when (confirmConflictResolution(analysis.conflicts.map { it.localFile.name }, newerSide = "local")) {
                    ConflictChoice.KEEP_LOCAL -> {
                        LauncherLog.log("Conflict: keep local — pushing local saves over cloud")
                        pushLocalOverCloud(c)
                        return true
                    }
                    ConflictChoice.CANCEL -> {
                        LauncherLog.log("Pull cancelled by user (local is newer for ${analysis.conflicts.size} file(s))")
                        return false
                    }
                    ConflictChoice.KEEP_REMOTE -> {
                        // Fall through: download EVERYTHING flagged (safe +
                        // conflicts), clobbering the newer local copies with
                        // cloud. All-or-nothing — never a mix of slots.
                        LauncherLog.log("Conflict: keep remote — overwriting local with cloud")
                    }
                }
                CloudSync.pullItems(this@LauncherActivity, c, analysis.toDownload + analysis.conflicts).collect { }
            } else {
                CloudSync.pullItems(this@LauncherActivity, c, analysis.toDownload).collect { }
            }
            return true
        } catch (t: Throwable) {
            LauncherLog.log("Pull failed: ${t.message ?: t.javaClass.simpleName}")
            android.util.Log.e("SilksongLauncher.Cloud", "pull flow failed ($source)", t)
            return true
        }
    }

    // ── Cloud save: push (Phase 1d) ────────────────────────────────────

    private fun onPushClicked() {
        val c = creds
        if (c == null) {
            LauncherLog.log("Push: not logged in")
            return
        }
        runCloudJob(spinPush, btnPush, R.string.action_push_saves_busy) { pushFlow(c) }
    }

    /**
     * Settings → auto-push, triggered from [onResume] when we just
     * came back from the game. Same execution path as the manual
     * Push button: a conflict prompt still appears if any local
     * file is older than the cloud copy.
     */
    private fun maybeAutoPush() {
        val c = creds ?: return
        if (!settings.autoPush) return
        LauncherLog.log("Auto-push: starting (returned from game, settings enabled)")
        runCloudJob(spinPush, btnPush, R.string.action_push_saves_busy) { pushFlow(c) }
    }

    private suspend fun pushFlow(c: TokenStore.Credentials) {
        try {
            LauncherLog.log("Analyzing local vs cloud saves…")
            val analysis = CloudSync.analyzePush(this@LauncherActivity, c)

            // Proceed when there's anything to do — uploads, conflicts,
            // OR cloud orphans to prune. Without the toDelete check we
            // bail out whenever nothing is queued for upload, so a
            // "delete-only" push (local pruned a rotated userN.dat.bakM
            // backup that cloud still holds, with no newer file to send)
            // would skip pushItems/toDelete entirely and leak the orphan.
            if (analysis.toUpload.isEmpty() &&
                analysis.conflicts.isEmpty() &&
                analysis.toDelete.isEmpty()
            ) {
                LauncherLog.log("Push: nothing to do (all ${analysis.skipped.size} local file(s) already match cloud, no orphans)")
                return
            }

            if (analysis.hasConflicts) {
                when (confirmConflictResolution(analysis.conflicts.map { it.localFile.name }, newerSide = "cloud")) {
                    ConflictChoice.KEEP_REMOTE -> {
                        LauncherLog.log("Conflict: keep remote — pulling cloud saves over local")
                        pullCloudOverLocal(c)
                        return
                    }
                    ConflictChoice.CANCEL -> {
                        LauncherLog.log("Push cancelled by user (cloud is newer for ${analysis.conflicts.size} file(s))")
                        return
                    }
                    ConflictChoice.KEEP_LOCAL -> {
                        // Fall through: upload EVERYTHING (safe + conflicts),
                        // clobbering the newer cloud copies with local.
                        LauncherLog.log("Conflict: keep local — overwriting cloud with local")
                    }
                }
                CloudSync.pushItems(c, analysis.all, toDelete = analysis.toDelete).collect { }
            } else {
                CloudSync.pushItems(c, analysis.toUpload, toDelete = analysis.toDelete).collect { }
            }
        } catch (t: Throwable) {
            LauncherLog.log("Push failed: ${t.message ?: t.javaClass.simpleName}")
            android.util.Log.e("SilksongLauncher.Cloud", "push flow failed", t)
        }
    }

    private enum class ConflictChoice { KEEP_LOCAL, KEEP_REMOTE, CANCEL }

    /**
     * Conflict resolver shown when local and cloud have diverged since
     * the last sync (one side is strictly newer than the other). Offers
     * a clear three-way choice instead of an ambiguous "overwrite?":
     *
     *   Keep local  → upload this device's saves, replacing the cloud.
     *   Keep remote → download the cloud saves, replacing this device.
     *   Cancel      → do nothing.
     *
     * Both directions are all-or-nothing — the winning side replaces the
     * other for the whole save set, never a per-slot mix. [newerSide] is
     * context for the message only ("the cloud copy is newer", etc.); the
     * choice itself is symmetric regardless of which button (Push or
     * Pull) opened the flow.
     */
    private suspend fun confirmConflictResolution(
        conflictFiles: List<String>,
        newerSide: String,
    ): ConflictChoice = kotlinx.coroutines.suspendCancellableCoroutine { cont ->
        fun finish(choice: ConflictChoice) {
            if (cont.isActive) cont.resumeWith(Result.success(choice))
        }
        val count = conflictFiles.size
        val msg = buildString {
            append("Your device and the cloud have both changed since the last sync ")
            append("(the $newerSide copy is newer).\n\n")
            append("Conflicting file(s):\n")
            for (name in conflictFiles.take(8)) append("• ").append(name).append('\n')
            if (count > 8) append("…and ").append(count - 8).append(" more\n")
            append("\nKeep local: upload this device's saves, replacing the cloud copy.\n")
            append("Keep remote: download the cloud saves, replacing this device's copy.")
        }
        android.app.AlertDialog.Builder(this@LauncherActivity)
            .setTitle("Save conflict")
            .setMessage(msg)
            .setPositiveButton("Keep local") { _, _ -> finish(ConflictChoice.KEEP_LOCAL) }
            .setNegativeButton("Keep remote") { _, _ -> finish(ConflictChoice.KEEP_REMOTE) }
            .setNeutralButton("Cancel") { _, _ -> finish(ConflictChoice.CANCEL) }
            .setOnCancelListener { finish(ConflictChoice.CANCEL) }
            .show()
    }

    /**
     * Forced full pull (no prompt): the user chose "keep remote", so
     * download the entire cloud set, clobbering local — including files
     * where local was newer. Re-analyses from scratch because the
     * originating flow may have been a push, which has no pull picture.
     */
    private suspend fun pullCloudOverLocal(c: TokenStore.Credentials) {
        val analysis = CloudSync.analyzePull(this@LauncherActivity, c)
        val items = analysis.toDownload + analysis.conflicts
        if (items.isEmpty()) {
            LauncherLog.log("Keep remote: nothing on cloud to pull")
            return
        }
        CloudSync.pullItems(this@LauncherActivity, c, items).collect { }
    }

    /**
     * Forced full push (no prompt): the user chose "keep local", so
     * upload the entire local set, clobbering cloud — including files
     * where cloud was newer. Re-analyses from scratch because the
     * originating flow may have been a pull, which has no push picture.
     */
    private suspend fun pushLocalOverCloud(c: TokenStore.Credentials) {
        val analysis = CloudSync.analyzePush(this@LauncherActivity, c)
        if (analysis.all.isEmpty() && analysis.toDelete.isEmpty()) {
            LauncherLog.log("Keep local: nothing local to push")
            return
        }
        CloudSync.pushItems(c, analysis.all, toDelete = analysis.toDelete).collect { }
    }

    // ── Settings ───────────────────────────────────────────────────────

    private fun onSettingsClicked() {
        startActivity(Intent(this, SettingsActivity::class.java))
    }

    // ── Cloud-job scheduling ───────────────────────────────────────────

    /**
     * Schedules [block] as the sole in-flight cloud job. If one is
     * already running we no-op (and log) — overlapping logins on the
     * same Steam account either race for the same CM session or
     * trip Steam's "another client signed in" guard. We also
     * disable the action buttons (Pull, Push, AND Launch) while a
     * job runs so the UI matches the underlying state — in
     * particular, auto-pull at startup needs to finish before the
     * user can launch the game, or the player gets dropped into a
     * still-stale local save.
     */
    /**
     * Runs one cloud operation, with the arrow on its button replaced by a
     * spinner for the duration.
     *
     * Both operations funnel through here, so the spinner has to be told which
     * button it belongs to. The label stays put and only the arrow is swapped:
     * a button whose text changes to "Downloading..." moves its own edges
     * about, and the pair is sized to sit on one row.
     */
    private fun runCloudJob(
        spinner: ProgressBar? = null,
        button: Button? = null,
        busyLabel: Int = 0,
        block: suspend () -> Unit,
    ) {
        if (cloudJob?.isActive == true) {
            LauncherLog.log("Cloud op already running — skipping")
            return
        }
        btnPull.isEnabled = false
        btnPush.isEnabled = false
        btnLaunch.isEnabled = false
        spinner?.visibility = View.VISIBLE
        if (busyLabel != 0) button?.text = getString(busyLabel)
        cloudJob = uiScope.launch {
            try {
                block()
            } finally {
                spinner?.visibility = View.GONE
                // Both, unconditionally: cheaper than remembering which one
                // was changed, and correct if a future caller changes both.
                btnPull.text = getString(R.string.action_pull_saves)
                btnPush.text = getString(R.string.action_push_saves)
                btnLaunch.isEnabled = true
                if (creds != null) {
                    btnPull.isEnabled = true
                    btnPush.isEnabled = true
                }
            }
        }
    }

    // ── Launch the game ────────────────────────────────────────────────

    private fun onLaunchClicked() {
        // Before anything else, because it is the one thing here that changes
        // what the player is about to run rather than what it will read.
        if (modsNeedBuilding()) {
            askAboutMods()
            return
        }
        continueLaunch()
    }

    /**
     * Whether the mods folder has moved on from the build that exists.
     *
     * Only the folder's contents, never the switches: turning a mod on or off
     * is applied at startup by the gate the weaver wove around it, so it is
     * already true of the build sitting on disk. Asking about that would be
     * offering a twenty-minute rebuild for something that has already
     * happened.
     */
    private fun modsNeedBuilding(): Boolean = try {
        val out = Il2cppConverter.rootFor(this)
        Il2cppConverter.isPresent(out) && Mods.isStale(Mods.dir(this), out, assets)
    } catch (t: Throwable) {
        // A folder that cannot be read is not a reason to refuse to play.
        LauncherLog.log("Could not check the mods folder: $t")
        false
    }

    /**
     * The offer, not the decision.
     *
     * "No" launches: a build is twenty minutes and somebody who has just sat
     * down to play has every right to leave a newly dropped mod until later.
     * The mod is simply not in the game until they say yes, which is what the
     * dialog says rather than implies.
     */
    private fun askAboutMods() {
        val mods = Mods.dir(this)
        val found = try { Mods.all(mods).size } catch (t: Throwable) { 0 }
        val what = if (found == 1) "A mod has" else "Mods have"
        android.app.AlertDialog.Builder(this)
            .setTitle("New mods detected")
            .setMessage(
                "$what been added, replaced or removed since the game was last built.\n\n" +
                    "Mods are compiled into the game, so applying the change means rebuilding. " +
                    "That takes several minutes — less than the first build, because only what " +
                    "actually changed is compiled again.\n\n" +
                    "Rebuild now, or play the build you already have?",
            )
            .setPositiveButton("Rebuild") { _, _ ->
                startActivity(
                    Intent(this, SetupActivity::class.java)
                        .putExtra(SetupActivity.EXTRA_REBUILD, true),
                )
                finish()
            }
            .setNegativeButton("Play anyway") { _, _ -> continueLaunch() }
            .setCancelable(true)
            .show()
    }

    /**
     * Launch button handler. Steam-style: if auto-pull is on and we're
     * logged in, sync the latest cloud saves DOWN first (resolving any
     * conflict via the dialog), then launch — so the player always
     * starts on the freshest save. Cancelling the conflict aborts the
     * launch. With auto-pull off or not logged in, we launch straight
     * away.
     */
    private fun continueLaunch() {
        val c = creds
        if (c == null || !settings.autoPull) {
            launchGame()
            return
        }
        runCloudJob(spinPull, btnPull, R.string.action_pull_saves_busy) {
            LauncherLog.log("Pre-launch sync: pulling latest cloud saves…")
            if (pullFlow(c, source = "pre-launch")) {
                launchGame()
            } else {
                LauncherLog.log("Launch aborted — unresolved save conflict")
            }
        }
    }

    private fun launchGame() {
        // The content is not inside the app: it is the depot's own bundle
        // tree, read through <files>/aa every time the game runs, and the
        // catalog can point nowhere else. So a depot that has been deleted or
        // moved is checked for here rather than being discovered by the engine
        // as an empty world, and the link is remade in case it moved.
        val depot = DepotLocation.resolve(this)?.takeIf { PlayerImage.depotData(it) != null }
        if (depot == null) {
            LauncherLog.log("Launch aborted: the game's files are not on this device")
            missingGameFiles()
            return
        }
        runCatching { DepotLocation.relink(this, depot) }
            .onFailure { LauncherLog.log("could not relink the content", it) }
        try {
            LauncherLog.log("Launching $UNITY_ACTIVITY_CLASS")
            // No FLAG_ACTIVITY_NEW_TASK / CLEAR_TASK and no finish()
            // here — we want Unity's activity ON TOP of ours in the
            // same task so the system back stack returns to us on
            // exit. Our `:launcher` process keeps us alive even
            // when Unity calls System.exit(0) on quit, so the
            // returning activity transition is just an OS-driven
            // resume of the still-living launcher.
            val intent = Intent().apply {
                setClassName(packageName, UNITY_ACTIVITY_CLASS)
            }
            // Written here rather than when the user changes a setting: this
            // is the moment the game will read them, so it is the moment they
            // cannot be stale.
            settings.exportForGame(this)
            // Same reason, and the more important one: this is the last moment
            // before the engine owns the save directory. See SaveDir -- the
            // game promotes a stranded save temp over the real save without
            // saying so, and clearing them is only possible from out here.
            SaveDir.prepare(this)
            returningFromGame = true
            startActivity(intent)
        } catch (t: Throwable) {
            returningFromGame = false
            LauncherLog.log("Failed to launch game: ${t.message}")
        }
    }

    /**
     * The depot has gone since the game was built.
     *
     * Worth a dialog rather than a log line, because from the outside this
     * looks like the app breaking on its own. The content was never copied
     * into the app -- it is several gigabytes and stays where the user put it
     * -- so deleting or moving that folder takes the game with it, and nothing
     * about the built engine says so.
     *
     * Nothing else is lost, and saying that matters: the toolchain, the
     * conversion and the compiled engine are all still there, so restoring the
     * folder or picking it again is minutes rather than the half hour the
     * first build took.
     */
    private fun missingGameFiles() {
        android.app.AlertDialog.Builder(this)
            .setTitle("The game's files are missing")
            .setMessage(
                "Silksong reads its content straight out of the folder you supplied. " +
                    "That folder is no longer there, so the game cannot start.\n\n" +
                    "Put it back, or point the app at it again. Everything else that was " +
                    "built is still here, so it will not have to be done again.",
            )
            .setPositiveButton("Find the files") { _, _ ->
                startActivity(Intent(this, SetupActivity::class.java))
                finish()
            }
            .setNegativeButton("Not now", null)
            .show()
    }
}
