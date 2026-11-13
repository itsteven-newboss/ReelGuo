//! Updates. Desktop: the Tauri updater downloads the signed release published in Admin → App 版本 and installs it.
//! Android: apps from outside a store can't replace themselves, so the viewer is offered the new APK to download.

use tauri::{AppHandle, Runtime};
use tauri_plugin_dialog::{DialogExt, MessageDialogButtons, MessageDialogKind};

use crate::shell::Bootstrap;

/// The release platform this build reports: windows-x86_64, darwin-aarch64, linux-x86_64, … or android.
pub fn platform_key() -> String {
    if cfg!(target_os = "android") {
        return "android".into();
    }
    let os = if cfg!(target_os = "windows") {
        "windows"
    } else if cfg!(target_os = "macos") {
        "darwin"
    } else {
        "linux"
    };
    format!("{os}-{}", std::env::consts::ARCH)
}

/// Shows a message box and waits for the answer (true = the first button).
async fn ask<R: Runtime>(app: &AppHandle<R>, title: &str, message: String, buttons: MessageDialogButtons) -> bool {
    let (tx, rx) = std::sync::mpsc::channel();
    app.dialog()
        .message(message)
        .title(title)
        .kind(MessageDialogKind::Info)
        .buttons(buttons)
        .show(move |ok| {
            let _ = tx.send(ok);
        });
    tauri::async_runtime::spawn_blocking(move || rx.recv().unwrap_or(false))
        .await
        .unwrap_or(false)
}

pub async fn offer<R: Runtime>(app: AppHandle<R>, origin: String, bootstrap: Bootstrap) {
    #[cfg(desktop)]
    desktop(app, origin, bootstrap.update_required).await;
    #[cfg(mobile)]
    android(app, origin, bootstrap).await;
}

#[cfg(desktop)]
async fn desktop<R: Runtime>(app: AppHandle<R>, origin: String, required: bool) {
    use tauri_plugin_updater::UpdaterExt;

    // Development builds never replace themselves.
    if cfg!(debug_assertions) {
        return;
    }
    let endpoint = format!("{origin}/api/v1/app/updates/{{{{target}}}}/{{{{arch}}}}/{{{{current_version}}}}");
    let Ok(endpoint) = url::Url::parse(&endpoint) else { return };
    let Ok(builder) = app.updater_builder().endpoints(vec![endpoint]) else { return };
    let Ok(updater) = builder.build() else { return };
    let update = match updater.check().await {
        Ok(Some(update)) => update,
        Ok(None) => return,
        Err(error) => {
            log::warn!("update check failed: {error}");
            return;
        }
    };

    let notes = update.body.clone().unwrap_or_default();
    let message = if required {
        format!("ReelGuo {} 已发布，需要更新后才能继续使用。\n\n{notes}", update.version)
    } else {
        format!("ReelGuo {} 已发布。\n\n{notes}", update.version)
    };
    let buttons = if required {
        MessageDialogButtons::OkCustom("立即更新".into())
    } else {
        MessageDialogButtons::OkCancelCustom("立即更新".into(), "稍后".into())
    };
    if !ask(&app, "发现新版本", message, buttons).await && !required {
        return;
    }
    match update.download_and_install(|_, _| {}, || {}).await {
        Ok(()) => app.restart(),
        Err(error) => {
            log::warn!("update failed: {error}");
            let _ = ask(&app, "更新失败", "新版本安装失败，请稍后重试，或从官网重新下载安装。".into(), MessageDialogButtons::Ok).await;
        }
    }
}

#[cfg(mobile)]
async fn android<R: Runtime>(app: AppHandle<R>, _origin: String, bootstrap: Bootstrap) {
    use tauri_plugin_opener::OpenerExt;

    if !bootstrap.update_available {
        return;
    }
    let Some(latest) = bootstrap.latest else { return };
    let required = bootstrap.update_required;
    let mut message = format!("ReelGuo {} 已发布。", latest.version);
    if required {
        message.push_str("需要更新后才能继续使用。");
    }
    if !latest.notes.is_empty() {
        message.push_str("\n\n");
        message.push_str(&latest.notes);
    }
    message.push_str("\n\n点击「下载」后用浏览器下载新版本并安装。");
    let buttons = if required {
        MessageDialogButtons::OkCustom("下载".into())
    } else {
        MessageDialogButtons::OkCancelCustom("下载".into(), "稍后".into())
    };
    if ask(&app, "发现新版本", message, buttons).await {
        let _ = app.opener().open_url(&latest.url, None::<&str>);
    }
    if required {
        app.exit(0);
    }
}
