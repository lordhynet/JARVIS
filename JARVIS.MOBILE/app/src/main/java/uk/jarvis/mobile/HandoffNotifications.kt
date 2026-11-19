package uk.jarvis.mobile
import android.Manifest
import android.app.*
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import uk.jarvis.mobile.contracts.CompanionHandoffView

internal object HandoffNotifications {
    private var shown: String? = null
    @Synchronized fun show(context: Context, view: CompanionHandoffView) {
        val manager = context.getSystemService(NotificationManager::class.java)
        val offer = view.offers.firstOrNull { it.incoming }
        if (offer == null) { manager.cancel(47); shown = null; return }
        if (shown == offer.handoffId || Build.VERSION.SDK_INT >= 33 && context.checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) return
        manager.createNotificationChannel(NotificationChannel("conversation-handoff", "Conversation transfers", NotificationManager.IMPORTANCE_DEFAULT).apply { setSound(null, null) })
        val open = PendingIntent.getActivity(context, 307, Intent(context, MainActivity::class.java), PendingIntent.FLAG_IMMUTABLE)
        manager.notify(47, Notification.Builder(context, "conversation-handoff").setSmallIcon(android.R.drawable.ic_btn_speak_now)
            .setContentTitle("Continue on phone?").setContentText("Open JARVIS and unlock to accept.")
            .setVisibility(Notification.VISIBILITY_PRIVATE).setContentIntent(open).setAutoCancel(true).build())
        shown = offer.handoffId
    }
}
