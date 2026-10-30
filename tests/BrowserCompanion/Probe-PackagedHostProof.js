async (page) => {
  const workers = page.context().serviceWorkers();
  if (workers.length !== 1) throw new Error("Expected one worker in the isolated proof profile.");
  return await workers[0].evaluate(async () => {
    if (chrome.runtime.getManifest().name !== "Media Controls lifecycle proof") {
      throw new Error("Unexpected browser extension.");
    }
    const port = chrome.runtime.connectNative("com.jpsoftworks.media_controls_lifecycle");
    return await new Promise(resolve => {
      const timer = setTimeout(() => {
        resolve({ timeout: true });
        port.disconnect();
      }, 5000);
      port.onMessage.addListener(message => {
        clearTimeout(timer);
        resolve(message);
        port.disconnect();
      });
      port.onDisconnect.addListener(() => {
        clearTimeout(timer);
        resolve({ error: chrome.runtime.lastError?.message });
      });
      port.postMessage({ type: "hello", version: 1, profileId: crypto.randomUUID(),
        epoch: crypto.randomUUID(), browser: "Proof" });
    });
  });
}
