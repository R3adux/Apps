import React, { useEffect, useRef } from 'react';
import { AppState, Linking, StatusBar, StyleSheet } from 'react-native';
import { SafeAreaProvider, SafeAreaView } from 'react-native-safe-area-context';
import { WebView } from 'react-native-webview';
import { Logger } from '@matter/main';
import { LightHub } from '../../core/hub.js';
import { eventScript, handleCall, injectedApi, replyScript } from './bridge';
import { selfTest } from './selftest';
import webHtml from './web-html';

const BACKGROUND = '#101217';
// The page is given as HTML; this origin only exists so the page has local storage.
// Nothing is ever loaded from it.
const PAGE_ORIGIN = 'https://licht.invalid/'; // reserved name: can never point to a real server
// The bridge to the hub is part of the page itself, so it is there before the page's own script
// runs. (Injecting it separately races with the page on Android: when the page was faster, it
// looked for Licht on a PC and reported "no connection".)
const PAGE_HTML = webHtml.replace('</head>', () => `<script>${injectedApi}</script>\n</head>`);
// After this long in the background, connections may be stale: check them right away.
const STALE_AFTER_MS = 15_000;

// Quiet by default; the self-test turns on more detail (see selftest.js).
Logger.level = 'warn';

// One hub for the whole app lifetime. Starting is quick: lamps are loaded from storage,
// connecting to them happens in the background.
const hub = new LightHub({ label: 'Licht Handy', id: 'licht-android' });
const hubReady = hub.start();
hubReady.catch((err) => console.error('Licht: Matter konnte nicht starten', err));

let pageConnected = false;

export default function App() {
  const webView = useRef(null);
  const send = (script) => webView.current?.injectJavaScript(script);

  // Live updates from the lamps → page
  useEffect(() => hub.subscribe((event) => {
    send(eventScript(event.type === 'lights' ? { type: 'lights', lights: hub.lights() } : event));
  }), []);

  // Back from the background: make sure the lamps still answer (and reconnect those that do not)
  // instead of waiting for a timeout. After a quick switch only lamps that are not connected are retried.
  useEffect(() => {
    let backgroundSince = 0;
    const subscription = AppState.addEventListener('change', (state) => {
      if (state !== 'active') {
        if (!backgroundSince) hubReady.then(() => hub.pause(), () => {});
        backgroundSince ||= Date.now();
        return;
      }
      const stale = Boolean(backgroundSince) && Date.now() - backgroundSince > STALE_AFTER_MS;
      backgroundSince = 0;
      hubReady.then(() => {
        hub.resume({ checkAll: stale });
        send(eventScript({ type: 'lights', lights: hub.lights() }));
      }, () => {});
    });
    return () => subscription.remove();
  }, []);

  // Automated test (CI): licht://selftest?code=…&ip=…
  useEffect(() => {
    Linking.getInitialURL().then((url) => {
      if (url?.startsWith('licht://selftest')) selfTest(hub, hubReady, url);
    });
  }, []);

  const onMessage = async (event) => {
    let message;
    try {
      message = JSON.parse(event.nativeEvent.data);
    } catch {
      return;
    }
    const { id, method, args } = message;
    if (!pageConnected) {
      pageConnected = true;
      console.log('LICHT_PAGE connected'); // the page talks to the hub (checked by the emulator test)
    }
    try {
      await hubReady;
      const result = await handleCall(hub, method, args ?? []);
      send(replyScript(id, true, result));
    } catch (err) {
      send(replyScript(id, false, { message: err?.message ?? String(err), status: err?.status ?? 500 }));
    }
  };

  // The page never navigates anywhere – block everything except the page itself.
  const onlyThePage = (request) => request.url === PAGE_ORIGIN || request.url.startsWith('about:')
    || request.url.startsWith('data:');

  return (
    <SafeAreaProvider>
      <SafeAreaView style={styles.root} edges={['top', 'bottom', 'left', 'right']}>
        <StatusBar barStyle="light-content" backgroundColor={BACKGROUND} />
        <WebView
          ref={webView}
          style={styles.root}
          originWhitelist={['*']}
          source={{ html: PAGE_HTML, baseUrl: PAGE_ORIGIN }}
          onMessage={onMessage}
          onShouldStartLoadWithRequest={onlyThePage}
          javaScriptEnabled
          domStorageEnabled
          setSupportMultipleWindows={false}
          overScrollMode="never"
          cacheEnabled={false}
        />
      </SafeAreaView>
    </SafeAreaProvider>
  );
}

const styles = StyleSheet.create({
  root: { flex: 1, backgroundColor: BACKGROUND },
});
