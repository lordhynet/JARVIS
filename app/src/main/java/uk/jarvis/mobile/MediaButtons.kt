package uk.jarvis.mobile

import android.content.Intent
import android.os.Build
import android.view.KeyEvent

@Suppress("DEPRECATION")
internal fun Intent.mediaKey(): KeyEvent? = if (action != Intent.ACTION_MEDIA_BUTTON) null
else if (Build.VERSION.SDK_INT >= 33) getParcelableExtra(Intent.EXTRA_KEY_EVENT, KeyEvent::class.java)
else getParcelableExtra(Intent.EXTRA_KEY_EVENT)
