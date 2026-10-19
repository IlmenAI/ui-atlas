# Raw Data Streams: selected surface query and recorded pixels

Raw Data Streams inspects one surface observation (bundle, frame, HWND) and its
UIA/MSAA responses. Raw World owns reconstruction; Semantic World owns reviewed
entities and assertions. The October 15 follow-up clarified that query scope and
screenshot capture scope are independent.

## Source association and selection

Keep controls in the query that returned them, including Office menu branches
reported by the main-window UIA query. Do not import another surface's records,
infer ownership, change parent links, or suppress geometry based on occlusion.
Provider attempts remain separate even when displayed together. Offscreen and
hidden records remain retained and can be shown explicitly.

The UIA and MSAA source buttons independently enable their records. UIA starts
on, MSAA starts off. Empty, failed, or container-only UIA does not silently enable
MSAA. Choices persist while navigating observations and surfaces. Both sources
can be enabled without merging their records, or both disabled while inspecting
the screenshot. Blue outlines identify UIA and green outlines identify MSAA.
Native and legacy provider names follow the same selection rules.

Detailed per-attempt selection remains under Capture and source details; Frame
streams exposes all retained evidence, including topology and failed attempts.

## Screenshot correspondence

The raw viewer first uses the same observation's recorded frame screenshot.
It crops that image using the selected surface and the enabled query records'
reported bounds. Viewport expansion is bounded by recorded image coverage and
uses those records only; it does not follow native ownership or other surfaces.
There is no stitching of independent HWND images, cross-frame reuse, or scene
reconstruction. Acquisition timing warnings remain visible when alignment was
not verified. Independent HWND images remain supporting evidence and a fallback
when the observation has no frame screenshot.

This repairs the regression where choosing an isolated Word HWND image omitted
menus that Word's main-window UIA response correctly reported. It also removes
the topology-derived crop veto from this raw display. The saved Colors, Fonts,
Effects, and Paragraph Spacing observations already contain the required pixels;
no recording or map migration is needed. Fonts extends below the native window,
so retaining the reported query bounds matters as well as selecting the image.

Controls and Overlay crop recorded pixels at the reported coordinates, including
static text, titles and small images. Show annotations independently adds the
accessibility names. Turning it off preserves text in the recorded image. Large
container descriptions do not replace visible text. Structure modes show
geometry without control crops.

## Validation contract

Regression coverage must check query isolation and actual screenshot/crop pixels
in all five modes. A control reported under the main query can legitimately use
menu pixels from that frame's screen capture. A separate popup query must never
inherit those main-query records. Additional checks cover bounds outside the
native window, explicit source toggles, empty UIA without MSAA fallback, static
text, annotations, and navigation.

Saved Word replay checks all four source selections (UIA, MSAA, both, neither)
across five modes and nine surface observations. Backgrounds and all rendered
control crops are compared byte-for-byte with the recorded screen image. The
original map and recording hashes must remain unchanged.
