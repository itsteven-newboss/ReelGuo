/**
 * The video elements on screen, so a sound change reaches them inside the tap or key press that asked for it.
 * Browsers (iOS Safari above all) only let a video become audible during such a gesture; a React effect after the
 * next render may already be too late.
 */
const live = new Set<HTMLVideoElement>();

export function registerVideo(video: HTMLVideoElement): () => void {
  live.add(video);
  return () => { live.delete(video); };
}

export function setLiveMuted(muted: boolean) {
  for (const video of live) video.muted = muted;
}

/**
 * Whether a tap, click or key press is happening right now (user activation). Unmuting without one makes Chrome pause
 * the video, so the first-interaction unmute checks it. Browsers without the API are given the benefit of the doubt.
 */
export function hasUserActivation(): boolean {
  const activation = (navigator as Navigator & { userActivation?: { isActive: boolean } }).userActivation;
  return activation ? activation.isActive : true;
}
