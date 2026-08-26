// MonoProvider — the way into the process the .NET runtime runs in.
//
// A ContentProvider is a strange thing to run a compiler from, and it is here
// for one property: an app may always reach its own provider. Everything else
// that creates a process on demand is a service, and a service may not be
// started by an app that is in the background -- startService throws
// IllegalStateException from API 26 onwards. A build runs a .NET program
// around twenty times over tens of minutes, so "the user pressed Home" is not
// an edge case, and the alternative is a foreground service, which means a
// permission and a permanent notification for something that is not the
// user's business.
//
// call() arrives on a binder thread in this process, which is the only part
// that matters: the process is :builder, it exists because this provider was
// reached, and it has a JavaVM because it is an app process. See monojni.c
// for why a JavaVM is not optional, and MonoRuntime for the rest.
//
// Nothing else about being a provider is used. There is no data here, and
// query/insert/update/delete are unreachable.

package dev.silksong.launcher

import android.content.ContentProvider
import android.content.ContentValues
import android.content.Context
import android.database.Cursor
import android.net.Uri
import android.os.Bundle
import java.io.File
import java.util.concurrent.atomic.AtomicBoolean
import kotlin.concurrent.thread

class MonoProvider : ContentProvider() {

    companion object {
        const val METHOD_RUN = "run"

        /** Ends this process. See [call]. */
        const val METHOD_QUIT = "quit"

        const val KEY_ASSEMBLY = "assembly"
        const val KEY_ARGS = "args"
        const val KEY_CWD = "cwd"
        const val KEY_BCL = "bcl"
        const val KEY_RUNTIME = "runtime"
        const val KEY_ENV = "env"
        const val KEY_OUT = "out"
        const val KEY_RESULT = "result"

        /** What is written to the result file when the run never started. */
        const val FAILED = 120

        /** Matches android:authorities in the manifest. */
        fun authority(context: Context): String = "${context.packageName}.mono"

        fun uri(context: Context): Uri = Uri.parse("content://${authority(context)}")
    }

    /** Set for the life of the process once a run has been accepted. */
    private val running = AtomicBoolean(false)

    override fun onCreate(): Boolean = true

    override fun call(method: String, arg: String?, extras: Bundle?): Bundle? {
        // Ends this process, whatever it is doing. The launcher asks for this
        // when a previous run's process is still here and the next run needs
        // one of its own -- a process only ever hosts a single run, so a
        // straggler is in the way and nothing here can talk it down. Reaching
        // it is never in doubt: the request only makes sense for a process
        // that exists, and this call arriving is the proof that it does.
        //
        // The kill is on another thread with a moment's delay because the
        // caller is blocked on a binder reply, and a process that has already
        // died does not send one. Losing that race is survivable -- the
        // launcher reads a dead provider as the process being gone, which is
        // what it asked for -- but there is no reason to lose it.
        if (method == METHOD_QUIT) {
            thread(name = "mono-quit") {
                Thread.sleep(100)
                android.os.Process.killProcess(android.os.Process.myPid())
            }
            return null
        }

        if (method != METHOD_RUN || extras == null) return null

        val assembly = extras.getString(KEY_ASSEMBLY)
        val args = extras.getStringArray(KEY_ARGS) ?: emptyArray()
        val cwd = extras.getString(KEY_CWD)
        val bcl = extras.getString(KEY_BCL)
        val runtime = extras.getString(KEY_RUNTIME)
        val env = extras.getStringArray(KEY_ENV) ?: emptyArray()
        val outPath = extras.getString(KEY_OUT)
        val resultPath = extras.getString(KEY_RESULT)

        if (assembly == null || bcl == null || runtime == null || outPath == null || resultPath == null) {
            return null
        }

        // One run per process, and this process only ever hosts one.
        //
        // A second run arriving while the first is going would not queue, it
        // would collide: the native side redirects stdout, rewrites the
        // environment and chdirs, all of which belong to the process and
        // would be pulled out from under the run already using them. A single
        // setup flow cannot do it, but a cancelled run leaves this process
        // alive for a moment, and the retry arrives here.
        if (!running.compareAndSet(false, true)) {
            runCatching {
                File(outPath).appendText("monojni: this process is already running another program\n")
                File(resultPath).writeText(FAILED.toString())
            }
            return null
        }

        // Returns at once; call() is a binder call and the caller is waiting
        // on it. The run is followed by tailing the output file, not by this.
        thread(name = "mono") {
            try {
                MonoBridge.load()
                // Writes the result file itself, including when the program
                // leaves through Environment.Exit and never comes back here.
                MonoBridge.nativeRun(assembly, args, cwd, bcl, runtime, env, outPath, resultPath)
            } catch (t: Throwable) {
                // The output file is the only channel back, and the launcher
                // is about to stop reading it, so anything worth saying has
                // to be appended now.
                runCatching {
                    File(outPath).appendText("monojni: ${t.javaClass.simpleName}: ${t.message}\n")
                }
                runCatching { File(resultPath).writeText(FAILED.toString()) }
            } finally {
                // The runtime cannot be initialised twice in a process, and
                // there are several programs to run, so the process goes
                // rather than being reused.
                android.os.Process.killProcess(android.os.Process.myPid())
            }
        }
        return null
    }

    // Not a provider of anything.
    override fun query(u: Uri, p: Array<String>?, s: String?, a: Array<String>?, o: String?): Cursor? = null
    override fun getType(uri: Uri): String? = null
    override fun insert(uri: Uri, values: ContentValues?): Uri? = null
    override fun delete(uri: Uri, s: String?, a: Array<String>?): Int = 0
    override fun update(uri: Uri, v: ContentValues?, s: String?, a: Array<String>?): Int = 0
}
