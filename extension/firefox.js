// Tạo manifest cho Firefox từ manifest.json dùng chung với Chrome/Edge.
export const FIREFOX_ID = "doma-download-manager@extension";
export const UPDATE_URL = "https://github.com/tdhieu94/download-manager/releases/latest/download/firefox-updates.json";

export function toFirefoxManifest(chrome, version) {
  const { key: _key, background, ...rest } = chrome;
  return {
    ...rest,
    version: version ?? chrome.version,
    background: { scripts: [background.service_worker], type: background.type },
    browser_specific_settings: {
      gecko: { id: FIREFOX_ID, strict_min_version: "128.0", update_url: UPDATE_URL },
    },
  };
}

export function updatesJson(version, xpiUrl, hash) {
  return {
    addons: {
      [FIREFOX_ID]: {
        updates: [{ version, update_link: xpiUrl, ...(hash ? { update_hash: `sha256:${hash}` } : {}) }],
      },
    },
  };
}
