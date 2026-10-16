// In-memory @react-native-async-storage/async-storage (v3 API).
function createAsyncStorage() {
  const data = new Map();
  return {
    getItem: async (k) => data.get(k) ?? null,
    setItem: async (k, v) => void data.set(k, v),
    removeItem: async (k) => void data.delete(k),
    getMany: async (keys) => Object.fromEntries(keys.map((k) => [k, data.get(k) ?? null])),
    setMany: async (entries) => { for (const [k, v] of Object.entries(entries)) data.set(k, v); },
    removeMany: async (keys) => { for (const k of keys) data.delete(k); },
    getAllKeys: async () => [...data.keys()],
    clear: async () => data.clear(),
  };
}
const legacy = createAsyncStorage();
module.exports = { createAsyncStorage, default: legacy, ...legacy };
