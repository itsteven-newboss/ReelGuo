// Runs before the page paints. With JavaScript on, the server-rendered page text (#seo) is for search engines and
// visitors without JavaScript only: the app replaces it, and the 18+ gate covers it until the visitor confirms.
document.documentElement.classList.add("rg-js");
