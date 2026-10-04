import React, { useEffect, useRef, useState } from 'react';
import { Linking, StatusBar, StyleSheet } from 'react-native';
import { SafeAreaProvider, SafeAreaView } from 'react-native-safe-area-context';
import { WebView } from 'react-native-webview';
import { Logger } from '@matter/main';
import { LightHub } from '../../core/hub.js';
import { eventScript, handleCall, injectedApi, replyScript } from './bridge';
import { selfTest } from './selftest';
import webHtml from './web-html';

const BACKGROUND = '#101217';

// Quiet by default; the self-test turns on more detail (see selftest.js).
Logger.level = 'warn';

// One hub for the whole app lifetime.
const hub = new LightHub({ label: 'Licht Handy', id: 'licht-android' });
const hubReady = hub.start().catch((err) => {
  console.error('Licht: Matter konnte nicht starten', err);
  throw err;
});

export default function App() {
  const webView = useRef(null);
  const [error, setError] = useState(null);

  // Live updates from the lamps → page
  useEffect(() => hub.subscribe((event) => {
    const payload = event.type === 'lights' ? { type: 'lights', lights: hub.lights() } : event;
    webView.current?.injectJavaScript(eventScript(payload));
  }), []);

  // Automated test (CI): licht://selftest?code=…&ip=…
  useEffect(() => {
    Linking.getInitialURL().then((url) => {
      if (url?.startsWith('licht://selftest')) selfTest(hub, hubReady, url);
    });
  }, []);

  useEffect(() => {
    hubReady.catch((err) => setError(err));
  }, []);

  const onMessage = async (event) => {
    let message;
    try {
      message = JSON.parse(event.nativeEvent.data);
    } catch {
      return;
    }
    const { id, method, args } = message;
    try {
      await hubReady;
      const result = await handleCall(hub, method, args ?? []);
      webView.current?.injectJavaScript(replyScript(id, true, result));
    } catch (err) {
      const reason = { message: err?.message ?? String(err), status: err?.status ?? (error ? 0 : 500) };
      webView.current?.injectJavaScript(replyScript(id, false, reason));
    }
  };

  return (
    <SafeAreaProvider>
    <SafeAreaView style={styles.root} edges={['top', 'bottom', 'left', 'right']}>
      <StatusBar barStyle="light-content" backgroundColor={BACKGROUND} />
      <WebView
        ref={webView}
        style={styles.root}
        originWhitelist={['*']}
        source={{ html: webHtml, baseUrl: 'https://licht.app/' }}
        injectedJavaScriptBeforeContentLoaded={injectedApi}
        onMessage={onMessage}
        javaScriptEnabled
        domStorageEnabled
        setSupportMultipleWindows={false}
        overScrollMode="never"
      />
    </SafeAreaView>
    </SafeAreaProvider>
  );
}

const styles = StyleSheet.create({
  root: { flex: 1, backgroundColor: BACKGROUND },
});
