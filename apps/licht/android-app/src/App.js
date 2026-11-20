import React, { useEffect, useRef } from 'react';
import { AppState, Linking, StatusBar, StyleSheet } from 'react-native';
import { SafeAreaProvider, SafeAreaView } from 'react-native-safe-area-context';
import { WebView } from 'react-native-webview';
import { eventScript, handleCall, injectedApi, replyScript } from './bridge';
import { mark, timings } from './timings';
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

// matter.js is loaded right after the first frame, not at start: evaluating it takes a moment (seconds on
// a slow phone), and until then the page already shows the lamps as last seen. Taps wait for the hub.
// One hub for the whole app lifetime; starting it is quick (lamps come from storage, connecting happens
// in the background).
let loading = null;
function loadHub() {
  if (!loading) {
    loading = new Promise((resolve) => setTimeout(resolve, 0)).then(() => {
      mark('matterLoading');
      const matter = require('./matter');
      mark('matterLoaded');
      console.log(`LICHT_TIMING matter.js loaded in ${timings.matterLoaded - timings.matterLoading} ms`);
      const hub = matter.createHub();
      const ready = hub.start().then(() => {
        mark('hubStarted');
        return hub;
      });
      ready.catch((err) => console.error('Licht: Matter konnte nicht starten', err));
      return { matter, hub, ready };
    });
    loading.catch((err) => console.error('LICHT_SELFTEST FAIL matter.js not loaded', err));
  }
  return loading;
}
const hubReady = () => loadHub().then(({ ready }) => ready);

let pageConnected = false;
let selfTestStarted = false;
let mounts = 0;

// When the lamps are connected (first one, all of them) – for "Messwerte".
function markConnected(hub) {
  const lights = hub.lights();
  if (lights.some((l) => l.status === 'online')) mark('firstLampConnected');
  if (lights.length && lights.every((l) => l.status === 'online')) mark('allLampsConnected');
}

export default function App({ processStart }) {
  const webView = useRef(null);
  const send = (script) => webView.current?.injectJavaScript(script);
  if (processStart && !timings.processStart) timings.processStart = processStart;

  // Live updates from the lamps → page (this also starts loading matter.js, after the first frame)
  useEffect(() => {
    let unsubscribe = () => {};
    let gone = false;
    loadHub().then(({ hub }) => {
      if (gone) return;
      unsubscribe = hub.subscribe((event) => {
        send(eventScript(event.type === 'lights' ? { type: 'lights', lights: hub.lights() } : event));
        markConnected(hub);
      });
    }, () => {});
    return () => {
      gone = true;
      unsubscribe();
    };
  }, []);

  // Back from the background: make sure the lamps still answer (and reconnect those that do not)
  // instead of waiting for a timeout. After a quick switch only lamps that are not connected are retried.
  useEffect(() => {
    // The app was closed while Licht stayed ready in the background (LichtService) and is opened again:
    // everything connected again.
    hubReady().then((hub) => {
      if (hub.paused && AppState.currentState === 'active') hub.resume();
    }, () => {});
    let backgroundSince = 0;
    const subscription = AppState.addEventListener('change', (state) => {
      if (state !== 'active') {
        if (!backgroundSince) hubReady().then((hub) => hub.pause(), () => {});
        backgroundSince ||= Date.now();
        return;
      }
      const stale = Boolean(backgroundSince) && Date.now() - backgroundSince > STALE_AFTER_MS;
      backgroundSince = 0;
      hubReady().then((hub) => {
        hub.resume({ checkAll: stale });
        send(eventScript({ type: 'lights', lights: hub.lights() }));
      }, () => {});
    });
    return () => subscription.remove();
  }, []);

  // Automated test (CI): licht://selftest?code=…&ip=…  – once per start of the app.
  useEffect(() => {
    console.log(`LICHT_APP shown (${++mounts}. time in this process)`); // shown in the emulator test log
    Linking.getInitialURL().then((url) => {
      if (!url?.startsWith('licht://selftest') || selfTestStarted) return;
      selfTestStarted = true;
      loadHub().then(({ matter, hub, ready }) => matter.selfTest(hub, ready, url), () => {});
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
      const hub = await hubReady();
      const result = await handleCall(hub, method, args ?? [], { timings });
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
          // The QR scanner shows the camera picture right after the user tapped "QR-Code scannen"; Android asks
          // for the camera permission itself (react-native-webview), the page never records anything.
          mediaPlaybackRequiresUserAction={false}
        />
      </SafeAreaView>
    </SafeAreaProvider>
  );
}

const styles = StyleSheet.create({
  root: { flex: 1, backgroundColor: BACKGROUND },
});
