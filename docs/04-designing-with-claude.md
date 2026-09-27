# Yo Mama — Designing the Workflows with Claude

> How to use Claude (Claude Code + Artifacts + design skills) to design the
> app's user flows *before* writing React Native code. The goal: go from
> "words in a doc" to clickable, agreed-on screens so you're not guessing when
> you scaffold.
>
> This doc gives you the **pipeline**, the **exact prompts**, and **what to ask
> for at each stage**, tailored to this app.
>
> **Update:** the client is now Unity (`05-unity-client.md`). Stages 1–5 still
> work as-is (HTML prototypes are the fastest way to agree on flows — also
> prototype the paywall and Party screens). For Step 6, ask Claude to convert
> the approved screen into uGUI + the scripts in `unity/` instead of React Native.

---

## 1. The design pipeline (what to make, in order)

Don't jump straight to pixel-perfect screens. Each stage de-risks the next:

```
1. User flows        → the decisions & screens, as a diagram        (cheap, fast)
2. Personas          → who swipes, what they want                   (optional but grounding)
3. Low-fi wireframes → boxes & labels, one HTML artifact            (agree on layout)
4. Clickable prototype → tappable HTML you can demo                 (feel the flow)
5. Visual design     → real styling, the "look"                     (commit to a vibe)
6. Handoff to RN     → Claude turns the approved artifact into      (build)
                       React Native components
```

**Rule of thumb:** stay in HTML artifacts as long as possible. They're instant,
free to throw away, and you can click them on your phone. Only move to React
Native once a flow is *approved*.

---

## 2. Tools you actually have

You're in Claude Code right now. Here's what each surface is for:

| Surface | Use it for | How you trigger it |
|---|---|---|
| **Claude Code (this chat)** | Reasoning about flows, writing these docs, generating artifacts, later scaffolding the RN code | Just ask |
| **Artifacts** | A rendered, shareable HTML page — wireframes, clickable prototypes, mockups | Ask Claude to "make an artifact" |
| **`persona-journey-prototyper` skill** | Personas + journey maps + low-fi clickable wireframes in one pass | Ask: "use the persona journey prototyper for the swipe flow" |
| **`artifact-design` skill** | Design fundamentals so artifacts look intentional, not default | Claude loads it automatically when building a designed artifact |
| **`dataviz` skill** | Any charts (e.g. an admin "top jokes" dashboard) | Ask for a chart/dashboard |

> **The key move:** ask Claude to build an **Artifact**. It renders as a real
> web page you can open, tap through, and send to a friend for feedback — no
> build step, no simulator.

---

## 3. The three core workflows to design

For yoMama specifically, design these three flows. Everything else is
secondary.

### A. First-open → first swipe (onboarding)
The make-or-break flow. No signup wall (anonymous-first, per `01-architecture.md`
§6). Screens: splash → (optional 1-card "how to swipe" hint) → the deck.

### B. The swipe loop (home)
The core product. Deck of cards, swipe right = love/save, left = pass, empty
state when the batch runs out, prefetch so it never stalls.

### C. Saved jokes + submit
Secondary screens: the "Saved" list (right-swiped jokes, un-save, share) and the
"Submit a joke" form (goes to the moderation queue).

Design these as **three separate artifacts** first, then stitch into one
clickable prototype.

---

## 4. Copy-paste prompts

Paste these into Claude Code (edit the bracketed bits). They're ordered to match
the pipeline in §1.

### Step 1 — User flow diagram

```
Read docs/01-architecture.md and docs/02-api-spec.md.
Make a user-flow diagram (Mermaid) for the "first-open to first swipe" flow
and the "swipe loop" flow. Show every screen and every decision point
(e.g. batch empty, network fails, swipe right vs left). Put it in an artifact
so I can view it.
```

### Step 2 — Personas & journey (optional, grounding)

```
Use the persona-journey-prototyper skill. Product: a Tinder-style app for
"yo mama" jokes. Design for two personas: a bored teen killing time, and a
someone hunting for a joke to text a friend. Give me personas, a journey map
for each, and low-fi wireframes for the swipe deck, saved list, and submit form.
```

### Step 3 — Low-fi wireframes

```
Make a low-fidelity wireframe artifact (grayscale, boxes and labels, no real
styling) for these three screens on a phone-sized frame:
1. Swipe deck (card stack, like/pass affordances, a counter)
2. Saved jokes list (each row: joke text, un-save, share)
3. Submit a joke (text field with 280-char counter, submit button, "goes to
   review" note)
Lay them side by side. Keep it obviously a wireframe — I want to agree on
layout, not colors.
```

### Step 4 — Clickable prototype

```
Turn those wireframes into a single clickable HTML prototype artifact.
Tapping "Saved" goes to the saved screen, "Submit" opens the form, and the
deck lets me actually drag/swipe a card left or right with the next card
appearing. Use placeholder jokes from docs/03-data-model.md §4. Mobile frame,
works on a phone browser.
```

### Step 5 — Visual design pass

```
Now do a visual design pass on the clickable prototype. Give it a fun, bold,
slightly irreverent look that fits a comedy app — but keep it readable and
accessible (contrast, tap targets >= 44px). Support light and dark. Show me the
deck, saved, and submit screens styled. Load the artifact-design skill.
```

If you're not sure what "look" you want, ask for options first:

```
Before styling, show me 3 distinct visual directions for this app as small
labeled mockups (e.g. "playful/cartoon", "clean/modern", "bold/street").
One artifact, side by side, so I can pick one.
```

### Step 6 — Handoff to React Native

```
I approve the [name] artifact. Convert the swipe-deck screen into a React
Native (Expo) component using react-native-gesture-handler + reanimated for
the swipe, matching the artifact's layout and styling. Wire it to the
GET /v1/jokes/feed and POST /v1/swipes endpoints from docs/02-api-spec.md
using TanStack Query. Keep the visual design from the artifact.
```

---

## 5. How to iterate (the loop that actually works)

Artifacts update in place — you don't start over each time. Effective feedback
is **specific and one-layer-at-a-time**:

- ✅ "The like button is too small — make it a full-width bar at the bottom."
- ✅ "On swipe-right, flash a green heart over the card for 300ms."
- ✅ "The empty state is boring — add a 'You've seen 'em all, submit your own?' CTA."
- ❌ "Make it better." (Claude will guess; you'll waste a round.)

Give feedback like a design review: point at the element, say what's wrong, say
what you want instead. Ask Claude to change **one screen or one interaction per
turn** so you can tell what changed.

> **Tip:** when an artifact is close, ask *"what's missing or unclear in this
> flow?"* — Claude will surface edge cases (offline, empty deck, duplicate
> swipe, moderation-pending confirmation) you'll otherwise discover the hard way
> in RN.

---

## 6. Edge cases & states to design (don't forget these)

A swipe app lives or dies on its non-happy states. Explicitly ask Claude to
design each:

| Screen | States to design |
|---|---|
| Swipe deck | loading first batch, mid-deck, **empty deck** (all seen), network error, prefetch stall |
| Swipe action | right (loved), left (passed), the brief confirmation animation |
| Saved list | has items, **empty** ("no saved jokes yet"), item removed |
| Submit | idle, typing (char counter), over 280, submitting, success ("in review"), rejected by blocklist |
| Auth | very first open (anon token being created, usually invisible) |

Prompt:

```
For the swipe deck screen, design all of these states in one artifact so I can
compare them: loading, active deck, empty deck, and network error. Label each.
```

---

## 7. From artifact to the real app

Once a flow is approved, the artifact becomes the spec. The handoff (Step 6)
works because Claude Code can read both the approved artifact *and* your API
spec, then generate RN components that match. Keep the approved artifacts around
— drop screenshots or the HTML into `docs/design/` so there's a visual record
of what was agreed.

Suggested folder:

```
docs/
  design/
    flows.md            # links/screenshots of the approved artifacts
    swipe-deck.html     # exported approved prototype (optional)
```

---

## 8. Quick reference — the whole thing in five asks

If you just want to move fast, these five prompts in order get you from nothing
to a buildable design:

1. `"Diagram the swipe app's core user flows as Mermaid in an artifact."`
2. `"Low-fi wireframe the deck, saved, and submit screens on a phone frame."`
3. `"Make it a single clickable prototype with a real draggable card."`
4. `"Visual design pass: bold comedy look, light+dark, accessible."`
5. `"Convert the approved deck screen to a React Native Expo component wired to the feed + swipe endpoints."`

Then come back here and say **"scaffold it"** — the design is no longer a guess.
```