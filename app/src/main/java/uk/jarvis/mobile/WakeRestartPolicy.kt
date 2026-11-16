package uk.jarvis.mobile

enum class WakeRestartAction { HandleCommand, OfferResume, Stop }

object WakeRestartPolicy {
    fun action(hasCommandIntent: Boolean, wakeWasEnabled: Boolean): WakeRestartAction = when {
        hasCommandIntent -> WakeRestartAction.HandleCommand
        wakeWasEnabled -> WakeRestartAction.OfferResume
        else -> WakeRestartAction.Stop
    }
}
