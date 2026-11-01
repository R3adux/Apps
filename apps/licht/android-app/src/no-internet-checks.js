// NetInfo (matter.js asks it for the Wi-Fi address) would otherwise sometimes check the internet connection
// by calling a Google server – Licht stays in the home network. Set up first thing, before anything uses NetInfo.
import NetInfo from '@react-native-community/netinfo';

NetInfo.configure({ reachabilityShouldRun: () => false, useNativeReachability: true });
