fn main() {
    // The addresses the app connects to are compiled in (see shell.rs); rebuild when they change.
    println!("cargo:rerun-if-env-changed=REELGUO_ORIGINS");
    tauri_build::try_build(
        tauri_build::Attributes::new().app_manifest(
            // Every command needs an explicit permission (capabilities/boot.json, and the runtime web-app capability).
            tauri_build::AppManifest::new().commands(&["boot_status", "retry", "set_fullscreen", "open_external"]),
        ),
    )
    .expect("failed to run tauri-build");
}
