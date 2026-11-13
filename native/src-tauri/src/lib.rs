//! ReelGuo native app (Phase 6): a thin shell around the ReelGuo web app.
//!
//! The window first shows a bundled start page, connects to the first reachable server address (the compiled-in list
//! plus the mirror list the server last sent), then loads the web app from there. Everything the viewer sees is the
//! web app itself, so sign-in, payments and media work exactly as on the web, and web deploys reach app users at once.
//! The shell adds what a browser tab can't: mirror fallback, real window fullscreen, external links in the system
//! browser, and updates (signed in-place updates on desktop, a download prompt for the APK on Android).

mod shell;
mod updates;

use std::sync::Mutex;

use tauri::{Manager, WebviewUrl, WebviewWindowBuilder};

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    #[allow(unused_mut)]
    let mut builder = tauri::Builder::default();

    #[cfg(desktop)]
    {
        // A second launch focuses the running window instead of opening another.
        builder = builder
            .plugin(tauri_plugin_single_instance::init(|app, _args, _cwd| {
                if let Some(window) = app.get_webview_window("main") {
                    let _ = window.unminimize();
                    let _ = window.set_focus();
                }
            }))
            .plugin(tauri_plugin_window_state::Builder::default().build())
            .plugin(tauri_plugin_updater::Builder::new().build())
            .plugin(tauri_plugin_process::init());
    }

    builder
        .plugin(tauri_plugin_opener::init())
        .plugin(tauri_plugin_dialog::init())
        .invoke_handler(tauri::generate_handler![
            shell::boot_status,
            shell::retry,
            shell::set_fullscreen,
            shell::open_external
        ])
        .setup(|app| {
            app.manage(shell::Boot(Mutex::new(shell::BootStatus::Connecting)));
            let handle = app.handle().clone();
            let navigation = handle.clone();
            let version = shell::app_version(&handle);

            let window = WebviewWindowBuilder::new(app, "main", WebviewUrl::App("index.html".into()))
                .initialization_script(&shell::bridge_script(&version))
                .on_navigation(move |url| shell::allow_navigation(&navigation, url));
            #[cfg(desktop)]
            let window = window
                .title("ReelGuo")
                .inner_size(1280.0, 800.0)
                // The web app switches to its desktop layout at 900 px.
                .min_inner_size(900.0, 600.0)
                .center();
            window.build()?;

            tauri::async_runtime::spawn(shell::connect(handle));
            Ok(())
        })
        .run(tauri::generate_context!())
        .expect("error while running ReelGuo");
}
