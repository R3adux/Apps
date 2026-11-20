package de.r3adux.licht

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.IBinder

/**
 * Keeps Licht running when the app is closed, so a tap (later: on the widget) switches at once instead of
 * starting the whole app first. The service itself does nothing: it only tells Android to keep the app's
 * process. In the background Licht sleeps (core/hub.js pause): no connections are kept busy, so the lamps
 * do not wake the phone.
 *
 * Android requires a notification for this. From Android 13 on it only shows when the app may post
 * notifications – Licht does not ask for that, so usually nothing is shown.
 */
class LichtService : Service() {

  override fun onBind(intent: Intent?): IBinder? = null

  override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
    try {
      val notification = notification()
      if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
        startForeground(NOTIFICATION_ID, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE)
      } else {
        startForeground(NOTIFICATION_ID, notification)
      }
    } catch (e: Exception) {
      // Android refused (should not happen): Licht works on, only not kept ready in the background.
      android.util.Log.w("Licht", "Background service refused: $e")
      stopSelf()
      return START_NOT_STICKY
    }
    return START_STICKY
  }

  private fun notification(): Notification {
    val open = PendingIntent.getActivity(
      this, 0, Intent(this, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP),
      PendingIntent.FLAG_IMMUTABLE,
    )
    val builder = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
      val channel = NotificationChannel(CHANNEL_ID, "Licht im Hintergrund", NotificationManager.IMPORTANCE_LOW).apply {
        description = "Hält Licht bereit, damit es sofort schaltet."
        setShowBadge(false)
      }
      getSystemService(NotificationManager::class.java)?.createNotificationChannel(channel)
      Notification.Builder(this, CHANNEL_ID)
    } else {
      @Suppress("DEPRECATION")
      Notification.Builder(this).setPriority(Notification.PRIORITY_LOW)
    }
    return builder
      .setSmallIcon(R.drawable.ic_stat_licht)
      .setContentTitle("Licht ist bereit")
      .setContentIntent(open)
      .setOngoing(true)
      .setShowWhen(false)
      .build()
  }

  companion object {
    private const val CHANNEL_ID = "hintergrund"
    private const val NOTIFICATION_ID = 1

    /** Starts the service; only works while the app is in front (Android's rule), so it is called from MainActivity. */
    fun start(context: Context) {
      val intent = Intent(context, LichtService::class.java)
      try {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) context.startForegroundService(intent)
        else context.startService(intent)
      } catch (e: Exception) {
        // Not allowed right now (e.g. started from the background): Licht still works, only not kept running.
        android.util.Log.w("Licht", "Background service not started: $e")
      }
    }
  }
}
