// Sets up matter.js for React Native (network, storage, crypto) – see @matter/react-native.
import './no-internet-checks';
import '@matter/react-native';
import { ReactNativeCrypto, ReactNativeWebStorageDriver } from '@matter/react-native';
import { Crypto, Entropy, Environment, StorageService } from '@matter/main';

const environment = Environment.default;

// The crypto provider registers itself on the environment that existed at import time;
// make sure the final default environment has it too.
const crypto = ReactNativeCrypto.provider();
environment.set(Entropy, crypto);
environment.set(Crypto, crypto);

// @matter/react-native 0.17.9 registers its storage driver as "react-native-web" but selects
// "async-storage" as default, which does not exist. Use the registered one.
const storage = environment.get(StorageService);
storage.registerDriver(ReactNativeWebStorageDriver);
storage.defaultDriver = ReactNativeWebStorageDriver.id;
