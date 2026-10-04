// Sets up matter.js for React Native (network, storage, crypto) – see @matter/react-native.
import '@matter/react-native';
import { ReactNativeCrypto } from '@matter/react-native';
import { Crypto, Entropy, Environment } from '@matter/main';

// The crypto provider registers itself on the environment that existed at import time;
// make sure the final default environment has it too.
const crypto = ReactNativeCrypto.provider();
Environment.default.set(Entropy, crypto);
Environment.default.set(Crypto, crypto);
