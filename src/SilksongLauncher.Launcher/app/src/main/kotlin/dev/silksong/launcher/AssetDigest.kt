// AssetDigest — what a staged asset hashes to, and what that deliberately ignores.
//
// Two markers in this app decide whether work already done is still good by
// hashing the on-device assets: SetupActivity.buildSignature, which owns "is
// the built game still the one this app would produce", and Mods.stamp, which
// owns the same question for the weaver. Both are cheap to write and expensive
// to get wrong -- a marker that says "different" when nothing is costs twenty
// minutes of somebody's evening, and it costs it silently, because the screen
// that results is indistinguishable from a device that was never built at all.
//
// They were wrong for a reason that has nothing to do with the build. A
// checkout on Windows has CRLF line endings, so the same commit stages the
// same C# with a couple of hundred extra bytes in it, and every one of those
// files hashes differently. Rebuilding the launcher on a Windows box therefore
// threw away a finished port: 46 of the 64 staged assets differed, and not one
// of them by anything the device would have compiled differently.
//
// So line endings are normalised out of the hash. Only for the files that are
// text: a .dll with its 0x0D bytes removed is not a .dll any more, and the
// whole point of hashing those is that a different weaver really does make a
// different game.

package dev.silksong.launcher

import android.content.res.AssetManager
import java.security.MessageDigest

object AssetDigest {

    private const val CR: Byte = 0x0D
    private const val LF: Byte = 0x0A

    /**
     * The staged assets that are source rather than payload.
     *
     * By extension rather than by sniffing for a NUL byte: the set is small,
     * fixed, and visible in app/build.gradle.kts beside the tasks that stage
     * it. A rule somebody can read is worth more here than one that is clever
     * about a file it has never met -- and guessing wrong in the direction of
     * "text" would corrupt the only signal we have about a binary.
     */
    private val TEXT = setOf("cs", "json", "sh", "txt", "rsp", "xml", "md")

    fun isText(name: String): Boolean =
        name.substringAfterLast('.', "").lowercase() in TEXT

    /**
     * CRLF to LF, and nothing else.
     *
     * A carriage return is dropped only where it precedes a newline, so a lone
     * 0x0D -- which is data wherever it turns up -- still changes the answer.
     * The array is returned unchanged when there was nothing to do, which is
     * the case on every Linux build and therefore the one worth not copying
     * for.
     *
     * Normalising rather than versioning the hash is what keeps this free:
     * content that is already LF hashes to exactly what it hashed to before,
     * so no device that is up to date is told to build again.
     */
    fun normalise(bytes: ByteArray): ByteArray {
        val out = ByteArray(bytes.size)
        var n = 0
        var changed = false
        for (i in bytes.indices) {
            if (bytes[i] == CR && i + 1 < bytes.size && bytes[i + 1] == LF) {
                changed = true
                continue
            }
            out[n++] = bytes[i]
        }
        return if (changed) out.copyOf(n) else bytes
    }

    /** Folds one asset into [md], with a text file's line endings normalised away. */
    fun update(md: MessageDigest, assets: AssetManager, path: String) {
        val bytes = assets.open(path).use { it.readBytes() }
        md.update(if (isText(path)) normalise(bytes) else bytes)
    }
}
