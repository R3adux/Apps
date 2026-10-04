package de.r3adux.licht

import android.content.Context
import android.net.wifi.WifiManager
import android.os.Bundle
import com.facebook.react.ReactActivity
import com.facebook.react.ReactActivityDelegate
import com.facebook.react.defaults.DefaultNewArchitectureEntryPoint.fabricEnabled
import com.facebook.react.defaults.DefaultReactActivityDelegate

class MainActivity : ReactActivity() {

  // Android drops multicast packets unless an app holds this lock. Matter needs them (mDNS)
  // to find lamps and to reconnect to them.
  private var multicastLock: WifiManager.MulticastLock? = null

  override fun getMainComponentName(): String = "Licht"

  override fun createReactActivityDelegate(): ReactActivityDelegate =
      DefaultReactActivityDelegate(this, mainComponentName, fabricEnabled)

  override fun onCreate(savedInstanceState: Bundle?) {
    super.onCreate(savedInstanceState)
    val wifi = applicationContext.getSystemService(Context.WIFI_SERVICE) as WifiManager
    multicastLock = wifi.createMulticastLock("licht-matter").apply {
      setReferenceCounted(false)
      acquire()
    }
  }

  override fun onDestroy() {
    multicastLock?.takeIf { it.isHeld }?.release()
    super.onDestroy()
  }
}
