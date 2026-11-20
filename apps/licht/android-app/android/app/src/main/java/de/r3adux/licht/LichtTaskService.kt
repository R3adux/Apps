package de.r3adux.licht

import android.content.Context
import android.content.Intent
import com.facebook.react.HeadlessJsTaskService
import com.facebook.react.bridge.Arguments
import com.facebook.react.jstasks.HeadlessJsTaskConfig

/**
 * Lets Licht's JavaScript run for a short while after the app was left (task "LichtBackground",
 * src/background.js). React Native stops all timers of the app while it is not visible – except while such
 * a task runs. Without it Licht could not fall asleep in the background (core/hub.js pause): the countdown
 * would never run out. The task ends as soon as Licht sleeps or the app is open again.
 */
class LichtTaskService : HeadlessJsTaskService() {

  override fun getTaskConfig(intent: Intent?): HeadlessJsTaskConfig {
    android.util.Log.i("Licht", "LICHT_TASK starting") // the emulator test log shows it
    return HeadlessJsTaskConfig(TASK, Arguments.createMap(), TIMEOUT_MS, true)
  }

  override fun onDestroy() {
    android.util.Log.i("Licht", "LICHT_TASK ended")
    super.onDestroy()
  }

  override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
    super.onStartCommand(intent, flags, startId)
    // If Android ends the app meanwhile, there is nothing left to put to sleep: do not start it again.
    return START_NOT_STICKY
  }

  companion object {
    private const val TASK = "LichtBackground"
    // Safety net: the task normally ends after about a minute (see core/hub.js SLEEP_AFTER_MS).
    private const val TIMEOUT_MS = 3 * 60 * 1000L

    /** Called when the app is no longer visible (MainActivity.onStop). */
    fun start(context: Context) {
      try {
        context.startService(Intent(context, LichtTaskService::class.java))
      } catch (e: Exception) {
        // Not allowed right now: Licht then simply stays awake in the background, as before.
        android.util.Log.w("Licht", "LICHT_TASK not started: $e")
      }
    }
  }
}
