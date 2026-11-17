package uk.jarvis.mobile

class CorePrivacyException(val eraseLegacy: Boolean = false) : Exception("Core access changed. Reconnect before starting voice.")

internal object CompanionAccessPolicy {
    fun eraseLegacyFor(error: Exception) = error is CorePrivacyException && error.eraseLegacy
}

/** Once invalidated, late responses from this client can never become current again. */
internal class PrivacyBoundary {
    @Volatile var epoch: Long? = null
        private set
    @Volatile private var invalid = false
    @Synchronized fun bind(value: Long) {
        if (invalid || value < 0 || epoch?.let { it != value } == true) {
            val changed = value >= 0 && epoch?.let { it != value } == true
            invalid = true; throw CorePrivacyException(changed)
        }
        epoch = value
    }
    @Synchronized fun verify(value: Long?, erased: Boolean = false) {
        if (invalid || erased || value == null || value < 0 || epoch?.let { it != value } == true) {
            val confirmed = erased || value != null && value >= 0 && epoch?.let { it != value } == true
            invalid = true; throw CorePrivacyException(confirmed)
        }
    }
    fun invalidate() { invalid = true }
}
