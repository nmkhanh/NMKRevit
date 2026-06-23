# Image To RebarFoundation JSON

Use this guide whenever new reinforcement drawing screenshots are provided and the goal is to produce a `RebarFoundation.json` file for the NMK Revit add-in.

## Output Contract

- Output one JSON document only, valid against `RebarFoundation.schema.json`.
- Use `schemaVersion: 2`, `units: "mm"`, and `coordinateSystem: "selectedSolidLocalXYZ"`.
- The Revit add-in does not read images. Images are interpreted before Revit, then converted into clean JSON.
- Do not invent unclear dimensions. Put unresolved or low-confidence reads in `reviewNotes`.
- Use `shape.kind: "straightWithNativeHooks"` only for 180-degree hooks. For 90-degree hooks and all other bends, use explicit line geometry through `uBar`, `hairpin`, or `piecewiseLine` because Revit hook orientation is not controllable enough for these details.
- Never assume a fixed number of reinforcement layers. Count the layers from the section/detail images. A project may have 4 layers total, 8 layers total, or another arrangement.

## Extraction Rules

1. Read every bar mark bubble such as `F1`, `F2`, `F10_1`.
2. Parse callouts in this form:

   ```text
   {quantity}-D{diameter}X{totalOrDevelopedLength}
   ```

   Example: `72-D25X10950` means quantity 72, diameter 25 mm, schedule/developed length 10950 mm.

3. Set `typeName` from the mark and diameter, for example `F1_D25`, `F10_D32`.
4. Set `sourceTypeName` to `CSS{diameter}`, for example `CSS10`, `CSS22`, `CSS35`.
5. Preserve split pieces as separate `barPieces` with one shared `parentMark`.
   Example: `F10_1`, `F10_2`, `F10_3`, `F10_4` all use `parentMark: "F10"`.
6. Preserve splice/coupler notes as string metadata only. Do not model couplers unless the user explicitly asks for that later.
7. Use explicit `offsetsMm`, `spacingPattern`, or `zones` when the drawing provides them. Use `maximumSpacingEven` only when the drawing gives a count and clear start/end limits but not every offset.
8. Treat `X####` in a callout as schedule/developed/cut length unless the same dimension line is clearly shown as the physical straight segment. Do not put developed length into `straightLengthMm` unless it is also the drawn straight segment.
9. For split bars, create one piece per physical segment. Use `hookStartMm` or `hookEndMm` for a one-sided leg; do not use `legLengthMm` unless the drawing shows legs at both ends.

## Layer And Offset Rules

Before writing any `z.offsetMm`, create a layer schedule from the section/detail images:

```text
Layer | Mark(s) | Reference face | Center offset to bar centre | Diameter | Direction | Evidence image/detail
```

Rules:

- `z.offsetMm` is always from the selected reference face to the **bar centreline**, not to the tangent, cover line, or drawing baseline.
- If the drawing gives a cover/tangent dimension, convert it to centre offset by adding or subtracting half the actual bar diameter as required.
- Keep top layers referenced to `topFace` and bottom layers referenced to `bottomFace`.
- Do not collapse marks into one offset just because they are near each other in a drawing. If the detail shows separate centreline offsets, write separate layers.
- If a mark appears in plan but its vertical layer is only visible in a section, use the section as the source of truth for `z.offsetMm`.
- If the image does not prove the layer count or offset, stop and add a `reviewNotes` item instead of guessing.

## Shape Mapping

- Straight bar with no hooks:

  ```json
  {
    "kind": "straight",
    "straightLengthMm": 10200
  }
  ```

- Straight bar with 180-degree hooks at one or both ends:

  ```json
  {
    "kind": "straightWithNativeHooks",
    "straightLengthMm": 10200,
    "hookStartMm": 150,
    "hookEndMm": 150,
    "hookAngleDegrees": 180,
    "fallbackToLine": true
  }
  ```

- U bar, 90-degree hooked bar, or split bar with explicit line legs:

  ```json
  {
    "kind": "uBar",
    "straightLengthMm": 9000,
    "legLengthMm": 1493,
    "hookLegDirection": "up"
  }
  ```

- Split bar with one leg only:

  ```json
  {
    "kind": "uBar",
    "straightLengthMm": 7507,
    "hookStartMm": 1493,
    "hookLegDirection": "up"
  }
  ```

  Use `hookStartMm` for a leg at the start of the drawn segment and `hookEndMm` for a leg at the end.

- Hairpin:

  ```json
  {
    "kind": "hairpin",
    "crownWidthMm": 179,
    "longLegMm": 1531,
    "shortLegMm": 150,
    "bendRadiusMm": 57
  }
  ```

## Placement Rules

- For horizontal layer bars, use:

  ```json
  {
    "direction": "X",
    "z": { "reference": "topFace", "offsetMm": 160 },
    "lineStartOffsetMm": 150,
    "lineEndOffsetMm": 150,
    "distribution": {
      "layout": "fixedSpacingWithRemainderAtEnd",
      "edgeStartMm": 150,
      "edgeEndMm": 150,
      "maximumSpacingMm": 145,
      "offsetsMm": [250, 383],
      "includeEnd": true
    }
  }
  ```

- `direction: "X"` means the bar itself runs along selected solid local X and is distributed along local Y.
- `direction: "Y"` means the bar itself runs along local Y and is distributed along local X.
- `z.reference` can be `topFace`, `bottomFace`, `absoluteFromBottom`, or `absoluteFromTop`.
- For hairpins, `distribution` is the longitudinal direction and `secondaryDistribution` is the row direction.

## Review Checklist

Before saving JSON:

- Every visible `F#` mark appears in `barMarks`.
- Every physical split piece appears in `barPieces`.
- `quantity` matches the number created by placement distributions.
- Every `sourceTypeName` follows `CSS{diameter}`.
- Only 180-degree hooks use `straightWithNativeHooks`; 90-degree hooks use explicit line geometry.
- Every top/bottom layer has an evidence-backed centre offset. Include all layers visible in the section, not just four default layers.
- For each split piece, `straightLengthMm + one-sided/both-sided leg lengths` must reconcile with the developed `X####` callout or be explained in metadata/review notes.
- Ambiguous OCR reads, missing dimensions, unclear splice lengths, and unsure host orientation are listed in `reviewNotes`.
