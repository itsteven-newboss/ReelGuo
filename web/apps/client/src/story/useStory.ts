import { unwrap, type Schemas } from "@reelguo/api-client";
import { useQuery } from "@tanstack/react-query";
import { useEffect, useMemo } from "react";
import { api } from "../api";
import { useAuth } from "../state/auth";
import { repairPlace, useStoryLocal, type Story, type StoryPlace } from "./story";

export type StoryNext = Schemas["StoryNextDto"];

const signedIn = () => useAuth.getState().status === "signed_in";

/** The story of an interactive series, with the signed-in viewer's saved place (refetched when they sign in or out). */
export function useStory(id: number | undefined, enabled: boolean) {
  const auth = useAuth((s) => s.status === "signed_in");
  return useQuery({
    queryKey: ["story", id ?? 0, auth],
    enabled: enabled && !!id,
    queryFn: async () => unwrap(await api.GET("/api/v1/series/{id}/story", { params: { path: { id: id! } } })),
    staleTime: 60_000,
  });
}

/** Tells the server about a step (counts the choice; saves a signed-in viewer's place). Never throws: the story goes on regardless. */
export async function sendNext(id: number, from: number, option: number | null, timedOut: boolean): Promise<StoryNext | null> {
  try {
    return unwrap(await api.POST("/api/v1/series/{id}/story/next", { params: { path: { id } }, body: { from, option, timed_out: timedOut } }));
  } catch {
    return null;
  }
}

/** Saves a signed-in viewer's place after going back or starting over. */
export async function savePlace(id: number, place: StoryPlace): Promise<void> {
  if (!signedIn()) return;
  try {
    unwrap(await api.PUT("/api/v1/series/{id}/story/progress", { params: { path: { id } }, body: { scene: place.scene, path: place.path, endings: place.endings } }));
  } catch {
    /* kept on the device; the next step saves it again */
  }
}

/**
 * The viewer's place in a loaded story. The device keeps a copy for everyone; when signed in, the account's place wins
 * on load, and a guest's progress is saved to the account the first time they open the story signed in.
 */
export function useStoryPlace(id: number, story: Story | undefined) {
  const local = useStoryLocal((s) => s.stories[id]);
  const setPlace = useStoryLocal((s) => s.setPlace);
  const rememberTitle = useStoryLocal((s) => s.rememberTitle);

  useEffect(() => {
    if (!story) return;
    const mine = useStoryLocal.getState().get(id);
    if (story.progress) {
      setPlace(id, repairPlace(story, story.progress));
    } else if (signedIn() && mine) {
      const place = repairPlace(story, mine);
      void savePlace(id, place);
    }
    for (const scene of story.scenes) {
      if (scene.ending_title) rememberTitle(id, scene.ep_num, scene.ending_title);
    }
  }, [id, story, setPlace, rememberTitle]);

  return useMemo(() => {
    if (!story) return null;
    const place = repairPlace(story, local ?? { scene: story.start, path: [], endings: [] });
    return { place, seen: local?.seen ?? [], titles: local?.titles ?? {} };
  }, [story, local]);
}
