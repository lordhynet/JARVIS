package uk.jarvis.mobile

internal class CompanionSetupException : IllegalStateException("Ask the owner to register this phone as an audio-only companion on Core.")
internal object CompanionRegistrationPolicy {
    fun shouldRegister(scopedCredential: Boolean, exists: Boolean, audioOnly: Boolean): Boolean {
        if (exists && audioOnly) return false
        if (scopedCredential) throw CompanionSetupException()
        return true
    }
}
