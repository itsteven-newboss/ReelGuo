//! Connecting to the server, the navigation policy, and the small bridge between the web app and the shell.

use std::{path::PathBuf, sync::Mutex, time::Duration};

use serde::{Deserialize, Serialize};
use tauri::{ipc::CapabilityBuilder, AppHandle, Manager, Runtime, State, WebviewWindow};
use tauri_plugin_opener::OpenerExt;
use url::Url;

/// The app version from tauri.conf.json: what the installer, the updater and the server all compare.
pub fn app_version<R: Runtime>(app: &AppHandle<R>) -> String {
    app.package_info().version.to_string()
}

#[cfg(desktop)]
pub const PLATFORM_LABEL: &str = "desktop";
#[cfg(mobile)]
pub const PLATFORM_LABEL: &str = "android";

/// Server addresses compiled into the app, in order of preference: `REELGUO_ORIGINS` at build time (comma-separated),
/// otherwise (unset or empty) the local development servers (debug) or the production placeholder (release).
const ORIGINS: &str = match option_env!("REELGUO_ORIGINS") {
    Some(list) if !list.is_empty() => list,
    // The Android emulator reaches the development machine at 10.0.2.2.
    _ if cfg!(debug_assertions) && cfg!(target_os = "android") => "http://10.0.2.2:4173,http://10.0.2.2:5173",
    _ if cfg!(debug_assertions) => "http://localhost:4173,http://localhost:5173",
    _ => "https://app.example.com",
};

const PROBE_TIMEOUT: Duration = Duration::from_secs(6);

#[derive(Clone, Serialize)]
#[serde(tag = "state", rename_all = "snake_case")]
pub enum BootStatus {
    Connecting,
    Connected { origin: String },
    Failed { tried: Vec<String> },
}

pub struct Boot(pub Mutex<BootStatus>);

/// What `/api/v1/app/bootstrap` answers: the current mirrors and the newest release for this platform.
#[derive(Deserialize)]
#[cfg_attr(desktop, allow(dead_code))] // latest and update_available drive the Android prompt; desktop asks the updater.
pub struct Bootstrap {
    pub mirrors: Vec<String>,
    pub latest: Option<Release>,
    pub update_available: bool,
    pub update_required: bool,
}

#[derive(Deserialize)]
#[allow(dead_code)] // url and notes are used by the Android prompt only.
pub struct Release {
    pub version: String,
    pub url: String,
    pub notes: String,
}

fn is_local(url: &Url) -> bool {
    matches!(url.host_str(), Some("localhost" | "127.0.0.1" | "10.0.2.2"))
}

/// "https://app.example.com/" → "https://app.example.com". Plain http is accepted only for local development servers.
pub fn normalize(origin: &str) -> Option<String> {
    let url = Url::parse(origin.trim()).ok()?;
    let secure = url.scheme() == "https" || (url.scheme() == "http" && is_local(&url));
    (secure && url.host_str().is_some()).then(|| url.origin().ascii_serialization())
}

fn mirrors_file<R: Runtime>(app: &AppHandle<R>) -> Option<PathBuf> {
    app.path().app_data_dir().ok().map(|dir| dir.join("mirrors.json"))
}

fn cached_mirrors<R: Runtime>(app: &AppHandle<R>) -> Vec<String> {
    mirrors_file(app)
        .and_then(|path| std::fs::read_to_string(path).ok())
        .and_then(|json| serde_json::from_str::<Vec<String>>(&json).ok())
        .unwrap_or_default()
}

fn save_mirrors<R: Runtime>(app: &AppHandle<R>, mirrors: &[String]) {
    if let Some(path) = mirrors_file(app) {
        if let Some(dir) = path.parent() {
            let _ = std::fs::create_dir_all(dir);
        }
        if let Ok(json) = serde_json::to_string(mirrors) {
            let _ = std::fs::write(path, json);
        }
    }
}

/// The server's latest mirror list first (it may have moved off a blocked domain), then the compiled-in addresses.
fn candidates<R: Runtime>(app: &AppHandle<R>) -> Vec<String> {
    let mut list: Vec<String> = Vec::new();
    for origin in cached_mirrors(app).iter().map(String::as_str).chain(ORIGINS.split(',')) {
        if let Some(o) = normalize(origin) {
            if !list.contains(&o) {
                list.push(o);
            }
        }
    }
    list
}

fn tls_config() -> rustls::ClientConfig {
    let mut roots = rustls::RootCertStore::empty();
    roots.extend(webpki_roots::TLS_SERVER_ROOTS.iter().cloned());
    rustls::ClientConfig::builder_with_provider(std::sync::Arc::new(rustls::crypto::ring::default_provider()))
        .with_safe_default_protocol_versions()
        .expect("ring supports the default TLS versions")
        .with_root_certificates(roots)
        .with_no_client_auth()
}

fn http_client(version: &str) -> reqwest::Result<reqwest::Client> {
    reqwest::Client::builder()
        .tls_backend_preconfigured(tls_config())
        .timeout(PROBE_TIMEOUT)
        .user_agent(format!("ReelGuoApp/{version} ({PLATFORM_LABEL})"))
        .build()
}

/// Reachable and really ours: the boot config names the app, or the region is blocked (the web app then explains).
async fn probe(client: &reqwest::Client, origin: &str) -> bool {
    let Ok(response) = client.get(format!("{origin}/api/v1/config")).send().await else {
        return false;
    };
    match response.status().as_u16() {
        200 => response
            .json::<serde_json::Value>()
            .await
            .map(|config| config.get("app_name").and_then(|n| n.as_str()) == Some("ReelGuo"))
            .unwrap_or(false),
        451 => true,
        _ => false,
    }
}

fn set_status<R: Runtime>(app: &AppHandle<R>, status: BootStatus) {
    if let Some(boot) = app.try_state::<Boot>() {
        *boot.0.lock().unwrap() = status.clone();
    }
    // The start page also asks on load, in case this happens before it is ready.
    let script = match status {
        BootStatus::Failed { .. } => "window.reelguoBoot && window.reelguoBoot.failed()",
        BootStatus::Connecting => "window.reelguoBoot && window.reelguoBoot.connecting()",
        BootStatus::Connected { .. } => return,
    };
    if let Some(window) = app.get_webview_window("main") {
        let _ = window.eval(script);
    }
}

/// Probes every candidate at once, then takes the first one in order of preference that answered.
pub async fn connect<R: Runtime>(app: AppHandle<R>) {
    set_status(&app, BootStatus::Connecting);
    let list = candidates(&app);
    let Ok(client) = http_client(&app_version(&app)) else {
        set_status(&app, BootStatus::Failed { tried: list });
        return;
    };

    let probes: Vec<_> = list
        .iter()
        .cloned()
        .map(|origin| {
            let client = client.clone();
            tauri::async_runtime::spawn(async move { probe(&client, &origin).await })
        })
        .collect();
    let mut chosen = None;
    for (origin, probe) in list.iter().zip(probes) {
        if matches!(probe.await, Ok(true)) {
            chosen = Some(origin.clone());
            break;
        }
    }

    let Some(origin) = chosen else {
        log::warn!("no server reachable; tried {list:?}");
        set_status(&app, BootStatus::Failed { tried: list });
        return;
    };
    open_web_app(&app, &origin);

    let app = app.clone();
    tauri::async_runtime::spawn(async move { after_connect(app, client, origin).await });
}

fn open_web_app<R: Runtime>(app: &AppHandle<R>, origin: &str) {
    let Some(window) = app.get_webview_window("main") else { return };
    // The web app's pages may call exactly two shell commands; nothing else is exposed to remote content.
    let id = format!("web-app-{}", origin.replace(|c: char| !c.is_ascii_alphanumeric(), "-"));
    let capability = CapabilityBuilder::new(id)
        .remote(format!("{origin}/*"))
        .window("main")
        .permission("allow-set-fullscreen")
        .permission("allow-open-external");
    if let Err(error) = app.add_capability(capability) {
        log::warn!("could not grant the web app its commands: {error}");
    }
    match Url::parse(&format!("{origin}/")) {
        Ok(url) => {
            let _ = window.navigate(url);
            set_status(app, BootStatus::Connected { origin: origin.to_string() });
        }
        Err(_) => set_status(app, BootStatus::Failed { tried: vec![origin.to_string()] }),
    }
}

/// Remembers the server's mirror list for the next start, then offers an update if there is one.
async fn after_connect<R: Runtime>(app: AppHandle<R>, client: reqwest::Client, origin: String) {
    let url = format!(
        "{origin}/api/v1/app/bootstrap?platform={}&version={}",
        crate::updates::platform_key(),
        app_version(&app)
    );
    let Ok(response) = client.get(url).send().await else { return };
    let Ok(bootstrap) = response.json::<Bootstrap>().await else { return };
    let mirrors: Vec<String> = bootstrap.mirrors.iter().filter_map(|m| normalize(m)).collect();
    if !mirrors.is_empty() {
        save_mirrors(&app, &mirrors);
    }
    crate::updates::offer(app, origin, bootstrap).await;
}

/// Top-level navigation: the web app and hosted payment pages (https), the bundled start page, local development
/// servers. mailto:/tel: go to the system; anything else (file:, intent:, custom schemes) is refused.
pub fn allow_navigation<R: Runtime>(app: &AppHandle<R>, url: &Url) -> bool {
    match url.scheme() {
        "https" | "tauri" | "about" | "data" | "blob" => true,
        "http" => is_local(url) || url.host_str() == Some("tauri.localhost"),
        "mailto" | "tel" => {
            let _ = app.opener().open_url(url.as_str(), None::<&str>);
            false
        }
        _ => false,
    }
}

/// Runs in every page the window loads (the start page and the web app):
/// - marks the user agent, so the web app knows it is inside the app (web/apps/client/src/native.ts);
/// - makes the window follow element fullscreen (desktop);
/// - sends new-tab links and window.open to the system browser.
pub fn bridge_script(version: &str) -> String {
    r#"(() => {
  if (window.__reelguoBridge) return;
  window.__reelguoBridge = true;
  const ua = navigator.userAgent + " ReelGuoApp/__VERSION__ (__PLATFORM__)";
  try { Object.defineProperty(navigator, "userAgent", { get: () => ua, configurable: true }); } catch (e) {}
  const invoke = (cmd, args) => {
    const internals = window.__TAURI_INTERNALS__;
    return internals ? internals.invoke(cmd, args).catch(() => {}) : Promise.resolve();
  };
  document.addEventListener("fullscreenchange", () => invoke("set_fullscreen", { on: !!document.fullscreenElement }));
  const external = (href) => {
    try {
      const url = new URL(href, location.href);
      if (url.protocol === "http:" || url.protocol === "https:") { invoke("open_external", { url: url.href }); return true; }
    } catch (e) {}
    return false;
  };
  window.open = (url) => { if (url) external(String(url)); return null; };
  document.addEventListener("click", (event) => {
    const link = event.target instanceof Element ? event.target.closest("a[target=_blank]") : null;
    if (link && link.href && external(link.href)) event.preventDefault();
  }, true);
})();"#
        .replace("__VERSION__", version)
        .replace("__PLATFORM__", PLATFORM_LABEL)
}

#[tauri::command]
pub fn boot_status(boot: State<'_, Boot>) -> BootStatus {
    boot.0.lock().unwrap().clone()
}

#[tauri::command]
pub fn retry(app: AppHandle) {
    tauri::async_runtime::spawn(connect(app));
}

#[tauri::command]
pub fn set_fullscreen(window: WebviewWindow, on: bool) {
    #[cfg(desktop)]
    let _ = window.set_fullscreen(on);
    #[cfg(mobile)]
    let _ = (window, on);
}

#[tauri::command]
pub fn open_external(app: AppHandle, url: String) -> Result<(), String> {
    let parsed = Url::parse(&url).map_err(|e| e.to_string())?;
    if !matches!(parsed.scheme(), "http" | "https") {
        return Err("only web links open externally".into());
    }
    app.opener().open_url(parsed.as_str(), None::<&str>).map_err(|e| e.to_string())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn origins_are_normalized_and_must_be_secure() {
        assert_eq!(normalize("https://app.example.com/some/path?q=1").as_deref(), Some("https://app.example.com"));
        assert_eq!(normalize(" https://app.example.com:8443 ").as_deref(), Some("https://app.example.com:8443"));
        assert_eq!(normalize("http://localhost:4173").as_deref(), Some("http://localhost:4173"));
        assert_eq!(normalize("http://app.example.com"), None);
        assert_eq!(normalize("ftp://app.example.com"), None);
        assert_eq!(normalize("not a url"), None);
    }

    #[test]
    fn the_bridge_script_carries_the_version() {
        let script = bridge_script("1.2.3");
        assert!(script.contains(&format!("ReelGuoApp/1.2.3 ({PLATFORM_LABEL})")));
        assert!(!script.contains("__VERSION__"));
    }
}
