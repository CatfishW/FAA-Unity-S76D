# Click-only pilot briefs

Runtime UI for the FAA simulation scene. **TFC BRIEF / WX BRIEF / CHART BRIEF / STATUS BRIEF** are one-click actions (action ids `Traffic`, `Weather`, `Chart`, `Status` in `ExplanationPilotActions.Names`; captions in `ExplanationPilotActions.Labels`). There is **no input field**, typed-question submission, or full-height workspace. Opening the brief, its options or a detail tab never starts a request.

- In Play Mode the brief is the flyout of the pilot chrome bar's **BRIEF** button and starts collapsed. It opens in the fixed left flyout slot (x 16, bottom 70 at the 1920 × 1080 reference scale, 8 above the bar), at most 468 wide and narrower when the registered IAS/TQ keep-outs move left (`FaaPilotChrome.FlyoutWidth`), so it never touches the left flight column, the boresight column or the heading lane. The action dock is 122 tall: **PILOT BRIEF** title, **OPTIONS**, the shared **CLOSE [Esc]** button, and a 2 × 2 grid of actions. The captions say BRIEF so they cannot be mistaken for the bar's TRAFFIC / WEATHER view buttons that turn the camera.
- The brief's state is a badge on the BRIEF button (WORKING, READY, CHECK, STOPPED, FAILED, NO ROOM); the button caption always reads BRIEF. Only one chrome flyout (brief, cues, commands, key list) is open at a time.
- While an unusual attitude is annunciated (`FaaRotorcraftConformalLayer.UnusualAttitudeActive`) the whole brief is hidden (alpha 0, no raycasts, keep-outs released) and returns unchanged on recovery.
- Without the chrome (edit-time hosts and the legacy UI tests) the 520 × 82 obstacle-avoiding dock remains, with single-row captions TRAFFIC / WEATHER / CHART / STATUS.
- An action opens a 196-tall result card directly above the dock and streams Picture / Context / Limit: relevant observations, supporting context and the main uncertainty. Technical metadata remains in Sources. An overlong draft gets at most one shortening pass within the existing four-round budget, using the same evidence and no new tools.
- Brief text uses fixed-height pages with visible Previous / Next controls and a page count. Longer provider output is never silently clipped; pages remain available even if the model ignores the requested 60-word limit. Sources and Tools have click-to-scroll controls as well as normal scrolling.
- SOURCES and TOOLS reuse the same small card (breadcrumb titles such as BRIEF / SOURCES; click the title to return). Click a source citation to inspect its captured values; tool activity shows observable actions, not private reasoning.
- REFRESH captures a new snapshot. STOP cancels the current request. Selecting another action cancels an in-progress brief and starts that selected action; clicking the same in-progress action does not send a duplicate request.
- The card's minus button closes just the result. CLOSE (or the BRIEF button) collapses the brief. Neither changes HUD, radar or screen-cue switches. Escape closes the card first, then the brief.
- Options exposes image-sharing and reduced-motion preferences. Its size stays compact, too.

The assistant canvas sorts above the HUD's UI canvases so instruments no longer render through the card. Its default geometry stays clear of the scene's primary flight instruments and radars; verify alternative/full-map layouts visually when their geometry changes.

## Provider and secrets

- Endpoint is pinned to `https://subtoken.shop/v1/chat/completions`; redirects are disabled. Default model: `subtoken-sonnet-4-6`, confirmed in the provider's model inventory and streaming tool-call probe.
- `FAA_EXPLANATIONS_API_KEY` is the runtime environment override. On macOS, the fallback is the login Keychain generic password with account `FAA`, service `FAA.Explanations.subtoken.shop`.
- Set the credential with Keychain Access or a non-echoing prompt; **never** put it in an asset, PlayerPrefs, `.env` committed to git, an Inspector field, or a question.
- Optional environment override: `FAA_EXPLANATIONS_MODEL`. The endpoint cannot be changed by model/tool output.
- No request runs automatically. Tapping an action or Refresh sends that fixed request and requested allowlisted snapshot records to the provider. If image sharing is enabled, scoped chart/radar source crops may also be sent. Disable images for metadata-only briefs. There are no whole-screen captures or arbitrary file/URL tools.
- Questions, answers, evidence and image bytes are in memory only. Image buffers are dropped on completion, failure, cancellation and the next question. Only the image-sharing and motion preferences use PlayerPrefs. Credentials and server response bodies are never logged.

## Evidence and tool harness

`ExplanationEvidenceCollector` copies the active XPlane12ApiHudBridge's allowlisted raw datarefs and validated engine conversions, TrafficRadarController targets, FAASectionalChartProvider metadata, radar/cue status, and existing VisualAnalysisManager cached results. Texture crops carry SHA-256 identity, dimensions, UV extent, capture time and source age. The chart image is a north-up source crop, not a screenshot of rotated overlays.

All seven tools are read-only and operate on **one frozen snapshot per question**. They accept only an optional short `reason` and the provider gateway's boolean `_` compatibility no-op (ignored), never commands, paths or URLs. Traffic is capped at the nearest 12 of the display-filtered set. The harness allows four model rounds and eight tool calls, 1,500 output tokens per round, a 75-second request timeout / 45-second idle timeout, bounded SSE buffers and cancellation. It assembles fragmented tool arguments and UTF-8 streams, handles parallel calls, and rejects incomplete streams, unknown tools, malformed arguments, provider errors and credential redirects. Duplicate image requests do not resend images.

Source-link checking confirms that citation IDs were actually returned to the model; **it does not establish semantic correctness**. Uncited/unresolved output is flagged. Every answer remains a fallible, point-in-time AI interpretation, never continuously live or a flight advisory.

## Important limits

- Missing or non-finite telemetry remains null; source age is feed-level, not independent per-field measurement validation.
- Range/cap-filtered traffic is incomplete; no targets does not establish clear airspace. Unreported aircraft types remain unknown. App threat classes are not certified TCAS advisories.
- Display visibility is separate from feed health and transmitter power.
- SIM WX spatial returns are illustrative, not measured storm locations. A rain ratio at the simulator aircraft is not a geolocated weather scan.
- When the weather display shows the simulator fallback's synthetic training cells (`XPlaneOriginalWeatherRadarDisplay.IsTrainingTexture` or `XPlaneOriginalWeatherRadarProvider.LastPublishWasSimulatorFallback`), the evidence says "synthetic training cells · local simulator fallback, not X-Plane weather" (`weather_spatial_source`, `weather_is_synthetic_training`, and the weather image state/limitations), so the brief can never present them as X-Plane weather or storms.
- Chart fetch time is not chart currency. No chart edition/effective date metadata is currently available. Raster/OCR content can be unreadable or incorrect.
- Existing visual-analysis cache entries have unknown alignment with the current image; their legacy fixed confidence numbers are deliberately omitted. Reading this cache never invokes the legacy provider or changes its configuration.
- No shell, arbitrary network/file access, control-surface, weather-setting or aircraft/autopilot tools are exposed. The workspace does not claim FAA certification or operational suitability.
- Chart-image interpretation is explicitly labeled AI interpretation. Prompts restrict it to prominent readable orientation features, prohibit operational frequency/vertical-limit/obstacle-height transcription, and prohibit locating ownship inside airspace from a north-up crop. A successful citation audit still cannot certify image interpretation.
- Turbulence evidence explicitly reports that this integration has no spatial turbulence scan. Regional settings, when fully available, are identified separately; missing samples are not zero turbulence. Rain does not establish turbulence.

## Validation

`FAA.Explanations.EditorTests` covers SSE/tool fragmentation, truncation/error handling, UTF-8 decoding, allowlists, privacy gates, missing data, immutable returned records, citation checks, fixed short-brief actions and their ALL CAPS ... BRIEF captions. Its UI fixtures also check absence of input fields, four actions, compact geometry, hide/restore, detail-tab sizing, layer order, unknown-action rejection, no automatic data capture and the synthetic-training-cell provenance wording. The docked flyout (slot, 2 × 2 captions, CLOSE, badge, declutter) is covered by `FaaPilotChromeTests.PilotBrief_StartsCollapsed_AndOpensInTheFixedSlotAboveTheBar`. Use the Unity CLI with the active FAA project to run this assembly. Runtime visuals and live action/streaming integration should also be checked in Play mode after changes.

For GUI-independent checks, `Tools/ExplanationVerification/ExplanationVerification.csproj` links the same pure test sources. Run it with a .NET 8 SDK and MSBuild properties `NewtonsoftPath` and `NUnitPath` pointing to the corresponding DLLs in the project's Unity PackageCache. No package download is needed. This runner is deliberately separate from the Unity UI tests.

The optional `--provider-smoke [historical-png-path]` argument performs a real, billed provider compatibility check using a clearly labeled test fixture and an optional explicitly selected historical image. It is never run by the normal unit suite. Do not supply an image you do not intend to send to subtoken.shop.
