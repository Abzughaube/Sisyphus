browser.browserAction.onClicked.addListener(() => {
  browser.tabs.query({ active: true, currentWindow: true }).then(tabs => {
    const url = tabs[0].url;
    fetch("http://localhost:5050/queue", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ url })
    })
    .then(response => {
      if (!response.ok) {
        alert("Fehler beim Senden: " + response.statusText);
      }
    })
    .catch(err => {
      alert("Verbindung zum lokalen Service fehlgeschlagen.");
      console.error(err);
    });
  });
});
