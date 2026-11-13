// The shell (src-tauri/src/shell.rs) connects in the background and then loads the web app in this window.
// This page only shows progress, and a retry button when no server address answered.
(() => {
  const connecting = document.getElementById("connecting");
  const failed = document.getElementById("failed");
  const invoke = (cmd) => (window.__TAURI_INTERNALS__ ? window.__TAURI_INTERNALS__.invoke(cmd) : Promise.reject(new Error("no shell")));

  const show = (state) => {
    connecting.hidden = state !== "connecting";
    failed.hidden = state !== "failed";
    if (state === "failed") document.getElementById("retry").focus();
  };

  // Called by the shell.
  window.reelguoBoot = { connecting: () => show("connecting"), failed: () => show("failed") };

  document.getElementById("retry").addEventListener("click", () => {
    show("connecting");
    invoke("retry").catch(() => show("failed"));
  });

  // The shell may have finished before this page loaded.
  invoke("boot_status")
    .then((status) => { if (status && status.state === "failed") show("failed"); })
    .catch(() => {});
})();
