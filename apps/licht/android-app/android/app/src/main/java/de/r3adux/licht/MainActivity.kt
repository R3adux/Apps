package de.r3adux.licht

import android.content.Context
import android.content.res.Configuration
import android.net.wifi.WifiManager
import android.os.Build
import android.os.Bundle
import android.os.Process
import android.os.SystemClock
import com.facebook.react.ReactActivity
import com.facebook.react.ReactActivityDelegate
import com.facebook.react.defaults.DefaultNewArchitectureEntryPoint.fabricEnabled
import com.facebook.react.defaults.DefaultReactActivityDelegate

class MainActivity : ReactActivity() {

  companion object {
    // For "Messwerte": how often this process built the window, and what changed before the last time
    // (bits of Configuration.diff, e.g. 0x80000000 = the app's resources).
    private var windowsBuilt = 0
    private var configurationChanges = 0
    private var lastConfiguration: Configuration? = null
  }

  // Android drops multicast packets unless an app holds this lock. Matter needs them (mDNS)
  // to find lamps and to reconnect to them.
  private var multicastLock: WifiManager.MulticastLock? = null

  // While Licht is on screen, the WLAN must not doze between packets: in power-save mode the phone
  // picks up the lamp's answers only every few hundred milliseconds, which makes Licht feel slow.
  // Held only while the app is visible, so it costs no battery otherwise.
  private var lowLatencyLock: WifiManager.WifiLock? = null

  override fun getMainComponentName(): String = "Licht"

  // The page shows how long starting took (menu "Messwerte"): it needs to know when the app's process began,
  // and how often Android built this window (and why it built it again).
  override fun createReactActivityDelegate(): ReactActivityDelegate =
      object : DefaultReactActivityDelegate(this, mainComponentName, fabricEnabled) {
        override fun getLaunchOptions(): Bundle = Bundle().apply {
          val processAge = SystemClock.elapsedRealtime() - Process.getStartElapsedRealtime()
          putDouble("processStart", (System.currentTimeMillis() - processAge).toDouble())
          putInt("windowsBuilt", windowsBuilt)
          putString("configurationChanges", "0x" + Integer.toHexString(configurationChanges))
        }
      }

  override fun onCreate(savedInstanceState: Bundle?) {
    // Before super.onCreate, which already starts the page (getLaunchOptions).
    val configuration = Configuration(resources.configuration)
    lastConfiguration?.let { configurationChanges = it.diff(configuration) }
    lastConfiguration = configuration
    windowsBuilt++
    super.onCreate(savedInstanceState)
    android.util.Log.i("Licht", "LICHT_ACTIVITY created ($windowsBuilt. time, restored: ${savedInstanceState != null}, " +
        "configuration changes: 0x${Integer.toHexString(configurationChanges)})")
    val wifi = applicationContext.getSystemService(Context.WIFI_SERVICE) as WifiManager
    multicastLock = wifi.createMulticastLock("licht-matter").apply { setReferenceCounted(false) }
    @Suppress("DEPRECATION")
    val mode = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) WifiManager.WIFI_MODE_FULL_LOW_LATENCY
               else WifiManager.WIFI_MODE_FULL_HIGH_PERF
    lowLatencyLock = wifi.createWifiLock(mode, "licht-fast").apply { setReferenceCounted(false) }
    // Keeps Licht ready when the app is closed (see LichtService).
    LichtService.start(this)
  }

  // Multicast only while Licht is visible: in the background it would keep the WLAN chip awake.
  override fun onStart() {
    super.onStart()
    multicastLock?.acquire()
  }

  override fun onStop() {
    multicastLock?.takeIf { it.isHeld }?.release()
    super.onStop()
    android.util.Log.i("Licht", "LICHT_ACTIVITY stopped")
    // Gives Licht's JavaScript the minute it needs to fall asleep (see LichtTaskService).
    LichtTaskService.start(this)
  }

  override fun onResume() {
    super.onResume()
    lowLatencyLock?.acquire()
  }

  override fun onPause() {
    lowLatencyLock?.takeIf { it.isHeld }?.release()
    super.onPause()
  }

  override fun onDestroy() {
    android.util.Log.i("Licht", "LICHT_ACTIVITY destroyed (finishing: $isFinishing, configuration change: $isChangingConfigurations)")
    multicastLock?.takeIf { it.isHeld }?.release()
    lowLatencyLock?.takeIf { it.isHeld }?.release()
    super.onDestroy()
  }
}
