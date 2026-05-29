# ADR 0002 - ONNX-assisted layout role detection spike

> **Status:** Draft / spike approved, implementation decision pending
> **Repository:** [redoxnet/pdfstruct](https://github.com/redoxnet/pdfstruct)
> **Goal:** Evaluate whether a local ONNX layout-role detector can improve PdfStruct by separating semantic role detection from reading-order reconstruction.

---

## 1. Context

### 1.1 What PdfStruct already does well

- Stable text and bounding-box extraction through PdfPig.
- Typography-driven heading detection based on font-size rarity, font-weight rarity, standalone rows, center alignment, vertical gaps, and line-count decay.
- XY-Cut-style multi-column reading order.
- Markdown and structured JSON output with per-element bounding boxes.
- Running header/footer filtering and list detection are already present as heuristic pipeline stages.

### 1.2 The two hard problems are different

| Problem | Nature | Label clarity | Best first tool |
|---|---|---|---|
| **Role detection** | Classification | Relatively clear; annotators often agree on body/title/table/header/footer/caption | Heuristics plus local ML priors |
| **Reading order** | Permutation | Often ambiguous; even humans disagree on complex layouts | Role-aware heuristics, then targeted ML only if needed |

Treating both problems as one mechanism makes the design harder to reason about. This ADR keeps them separate.

### 1.3 Working hypothesis

**If block roles are known, many reading-order failures become easier.**

Examples:

- `PageHeader`, `PageFooter`, and `PageNumber` can be excluded from the main stream.
- `Figure`, `Table`, and their captions can be grouped as side units rather than forced through body-flow ordering.
- `Footnote` blocks can be held aside and reinserted at page-end or anchor positions.
- `Title` and `SectionHeader` can anchor the body stream.
- The remaining `Body` and `ListItem` blocks can be passed to XY-Cut with less noise.

This is still a hypothesis. It must be tested before adding ONNX dependencies.

---

## 2. Decision

### 2.1 Run a validation spike before accepting ONNX as product architecture

ONNX is a plausible fit, but the product decision is not accepted yet. First we run an oracle-role experiment:

1. Select 3-5 weak fixtures.
2. Manually label extracted blocks with coarse roles.
3. Feed those oracle roles into a role-aware reading-order prototype.
4. Compare the result with the current parser output.

If oracle roles materially improve the weak cases, role classification is a real bottleneck and ONNX integration can proceed. If oracle roles do not help, reading order itself is the bottleneck and ONNX should be postponed.

### 2.2 Keep SaaS VLMs out of scope

Hosted VLMs such as Claude or Gemini are rejected for this stage:

- RAG indexing is batch-oriented and can multiply cost and latency.
- Documents may contain private or regulated data.
- PdfStruct's positioning is local, deterministic, and dependency-light.
- A local layout classifier is enough for this validation stage.

### 2.3 Keep ONNX optional if the spike succeeds

If accepted later, ONNX must remain opt-in and outside the core package.

Proposed package shape:

```text
PdfStruct                         core package; pure .NET + PdfPig
PdfStruct.Layout.Onnx             optional CPU ONNX Runtime package
PdfStruct.Layout.Onnx.DirectML    optional Windows GPU acceleration add-on, if justified
PdfStruct.Layout.Onnx.Cuda        optional NVIDIA CUDA add-on, if justified
```

The core package must not gain an ONNX Runtime dependency.

---

## 3. External Model Assessment

### 3.1 Candidate model

| Model | License | Notes | Decision |
|---|---|---|---|
| **PP-DocLayout-S** | Apache-2.0 | Lightweight layout detector with 23 classes including document title, paragraph title, text, page number, table, table caption, image, figure caption, header, footer, footnote, and aside text. Public distribution currently appears Paddle-format first, so ONNX conversion must be verified. | Best first candidate |
| ResNet18 / MobileNetV3 block classifier | Depends on training data | Natural block-crop classifier shape, but requires our own training dataset. | Later fallback |
| DocLayout-YOLO | AGPL-3.0 | Stronger license constraints for NuGet distribution. | Reject |
| VLMs such as SmolDocling | Varies; much larger | Too large for the current library boundary. | Out of scope |

### 3.2 ONNX Runtime implications

- CPU ONNX Runtime is available through `Microsoft.ML.OnnxRuntime` and is the only baseline dependency we should consider for the optional package.
- Execution providers such as CUDA, DirectML, and CoreML exist, but they should not be described as automatic behavior of the core package.
- DirectML is supported, but current ONNX Runtime documentation describes it as sustained engineering; treat it as an optional acceleration path, not the main architecture.
- Vulkan is not a first-class ONNX Runtime execution provider for this plan and should not influence the design.

### 3.3 Model artifact risk

Before Phase 1.5b, verify:

- Whether PP-DocLayout-S can be converted cleanly to ONNX with Paddle2ONNX or PaddleX.
- The resulting ONNX opset and whether CPU Runtime can execute it.
- Whether DirectML can execute it if we later consider the DirectML package.
- The actual artifact size after conversion and optional slimming.
- The exact license files and attribution requirements that must ship in the NuGet package.

---

## 4. Current PdfStruct Fit

### 4.1 Important pipeline constraint

The current parser performs reading-order analysis before element classification. A role detector cannot improve reading order if it is only added as a prior inside the existing `FontBasedElementClassifier`.

Therefore, a successful design probably needs one of these shapes:

1. **Page role annotation pass:** detect coarse roles immediately after block construction, before final page ordering.
2. **Two-pass layout:** run current ordering, detect roles, then run role-aware reordering.
3. **Detector-first page pass:** run page-level layout detection on rendered pages, map detected regions to PdfPig blocks, then order by role-aware streams.

The spike should test this with oracle roles before introducing model code.

### 4.2 Existing extension points

Existing APIs that matter:

- `ILayoutAnalyzer.DetermineReadingOrder(IReadOnlyList<TextBlock>)`
- `IElementClassifier.Classify(IReadOnlyList<DocumentTextBlock>, ref int startId)`
- `CompositeElementClassifier`
- `RegexHeadingClassifier`
- `FontBasedElementClassifier.AnalyzeHeadings(...)`

The current classifier stack is document-sequence oriented and produces final content elements. A layout-role detector is different: it should annotate blocks before final element production.

---

## 5. Proposed Interfaces

### 5.1 Prefer page-level role detection over per-block async classification

PP-DocLayout-S is a page-level detector. Running a per-block async classifier would either repeat page inference unnecessarily or hide caching behind the interface. Prefer a page-level API:

```csharp
namespace PdfStruct.Analysis;

public interface IPageRoleDetector
{
    string DetectorId { get; }

    ValueTask<IReadOnlyList<BlockRolePrediction>> DetectAsync(
        PageRoleDetectionInput page,
        CancellationToken cancellationToken = default);
}

public sealed record PageRoleDetectionInput(
    int PageNumber,
    double PageWidth,
    double PageHeight,
    IReadOnlyList<TextBlock> Blocks,
    Stream? PageRaster = null);

public sealed record BlockRolePrediction(
    int BlockIndex,
    BlockRoleScores Scores,
    string? SourceLabel = null,
    float Confidence = 0);

public readonly record struct BlockRoleScores(
    IReadOnlyDictionary<BlockRole, float> Scores);

public enum BlockRole
{
    Body,
    Title,
    SectionHeader,
    ListItem,
    Table,
    TableCaption,
    Figure,
    FigureCaption,
    Footnote,
    PageHeader,
    PageFooter,
    PageNumber,
    Aside,
    Abandoned
}
```

### 5.2 Options

Use explicit opt-in rather than package-reference side effects:

```csharp
public sealed class PdfStructOptions
{
    public IList<IPageRoleDetector> PageRoleDetectors { get; } = [];
}
```

Example, if the optional package is installed:

```csharp
var options = new PdfStructOptions();
options.PageRoleDetectors.Add(
    new OnnxPageRoleDetector(modelPath: "models/pp-doclayout-s.onnx"));

var parser = new PdfStructParser(options);
```

### 5.3 Role priors for heading classification

Role detection can still feed heading classification, but only as one prior among existing typography signals.

Suggested behavior:

- `SectionHeader` and `Title` raise heading probability.
- `Body`, `Table`, `Figure`, `Footnote`, and page furniture lower heading probability.
- Missing detector output leaves current behavior unchanged.
- Regex/domain classifiers, especially for Korean legal structures, retain precedence when configured.

---

## 6. Validation Spike

### 6.1 Preliminary proxy result (2026-05-03)

Before building a full oracle-label harness, we ran the existing heading diagnostic pipeline and applied a coarse role-proxy demotion rule to current heading candidates. This is not the final oracle experiment, but it gives an early signal about whether role information targets real failure clusters.

| Fixture | Blocks | Current headings | Coarse role-proxy demotions | Remaining headings | Interpretation |
|---|---:|---:|---:|---:|---|
| `table_of_contents.pdf` | 42 | 13 | 11 page-number/furniture headings | 2 | Very strong signal: most false headings are page-number roles. |
| `magazine_article.pdf` | 14 | 2 | 1 byline/furniture heading | 1 | Pull quote is already controlled; byline still needs role demotion. |
| `plos_utilizing_llm.pdf` | 299 | 61 | 48 metadata/caption/reference/author headings | 13 | Strong signal: role labels would remove a large false-positive cluster. |
| `plos_game_based_education.pdf` | 381 | 66 | 30 metadata/caption/reference/author headings | 36 | Moderate-to-strong signal; remaining cases need manual review. |
| `kr_constitution.pdf` | 264 | 1 | 0 | 1 | Confirms Korean legal hierarchy still needs regex/domain rules, not generic visual roles alone. |

Initial conclusion: role information is likely worth pursuing for furniture, captions, references, and publication metadata. It is not a replacement for domain-specific heading classifiers such as the Korean legal regex path.

### 6.2 Fixtures

Use existing fixtures first where possible:

- `kr_constitution.pdf`: tests legal headings where typography is weak and regex/domain rules remain important.
- `table_of_contents.pdf`: tests page-number columns and TOC structure.
- `magazine_article.pdf`: tests pull quotes and aside text.
- `plos_utilizing_llm.pdf` or `plos_game_based_education.pdf`: tests academic multi-column layout and figure/table captions.

Additional fixture candidates:

- US patent front page with INID codes and two-column metadata flow.
- A page with mid-body figure/table captions.

### 6.3 Oracle role file

Create a small JSON file mapping extracted block identity to role. Prefer stable fields over full text when possible:

```json
{
  "fixture": "table_of_contents.pdf",
  "pages": {
    "1": [
      { "match": "^Contents$", "role": "Title" },
      { "match": "^\\d+(\\s+\\d+)*$", "role": "PageNumber" },
      { "match": ".+\\.{3,}\\s*\\d+$", "role": "Body" }
    ]
  }
}
```

The first spike can use pattern-based oracle labels if manually audited. The final validation set should store block-level labels generated from diagnostics.

### 6.4 Prototype behavior

For each page:

1. Build the current page blocks.
2. Assign oracle roles.
3. Split blocks into streams:
   - furniture: `PageHeader`, `PageFooter`, `PageNumber`, `Abandoned`
   - side units: `Figure`, `FigureCaption`, `Table`, `TableCaption`, `Aside`
   - footnotes: `Footnote`
   - main: `Title`, `SectionHeader`, `Body`, `ListItem`
4. Run XY-Cut only on the main stream.
5. Reinsert side units and footnotes by simple anchor rules.
6. Compare against current output.

### 6.5 Success criteria

Continue toward ONNX only if at least two weak fixtures improve without a clear regression in the others.

Suggested measurements:

- TOC page-number blocks no longer become headings.
- Pull quotes are classified as `Aside` or `Body`, not headings.
- Academic captions stay near their figure/table anchors.
- Korean legal heading recognition does not regress when regex patterns are configured.
- Main body article/order monotonicity remains stable.

---

## 7. Implementation Plan If The Spike Succeeds

### Phase 1.5a - Core role annotation support

- Add `BlockRole`, `BlockRoleScores`, and a page-level detector interface.
- Add optional role predictions to an internal block/page context rather than final public content elements first.
- Add diagnostics that emit block geometry, text prefix, assigned role, source detector, and confidence.
- Add heading-fusion logic that consumes role priors but preserves current behavior when no role detector is configured.
- Keep existing fixtures passing.

### Phase 1.5b - Optional ONNX package

- Create `PdfStruct.Layout.Onnx`.
- Depend on `Microsoft.ML.OnnxRuntime` only in that package.
- Verify and document PP-DocLayout-S conversion to ONNX.
- Implement page rasterization and detector output parsing.
- Map detector labels to `BlockRole` through an explicit table.
- Match detected regions to PdfPig blocks by IoU plus containment heuristics.
- Add package-level license and model attribution files.

### Phase 1.5c - Weak-fixture regression tests

- Add oracle labels and/or detector snapshots for weak fixtures.
- Verify opt-in ONNX behavior separately from default parser behavior.
- Ensure no ONNX dependency appears in the core package graph.

### Phase 2 - Role-aware reading order

- Split page blocks by role before ordering.
- Apply XY-Cut to main stream only.
- Group figures/tables with adjacent captions.
- Reinsert side units and footnotes by anchor position.
- Preserve list detection and heading-level assignment behavior.

---

## 8. Open Questions

1. **Model artifact distribution**
   - Bundle the ONNX file in the optional NuGet package for offline reliability, or download it on first use to keep package size small?

2. **Model choice**
   - Does PP-DocLayout-S work well enough after ONNX conversion and IoU block mapping, or do we need a smaller block-crop classifier trained for PdfStruct roles?

3. **Rendering dependency**
   - Which renderer should produce page rasters for ONNX input while keeping the core package dependency-light?

4. **Interface stability**
   - Should `BlockRole` be public in Phase 1.5a, or should it remain internal until role-aware ordering has settled?

5. **Package naming**
   - `PdfStruct.Layout.Onnx`, `PdfStruct.Onnx`, or `PdfStruct.Classifiers.Onnx`?

6. **Spike result**
   - Does oracle role information actually improve reading order on the selected weak fixtures?

---

## 9. Immediate Actions

1. Convert this ADR to English and record it as the ONNX spike plan.
2. Commit the ADR separately.
3. Build a minimal oracle-role experiment using existing fixtures.
4. Record spike results in this ADR before accepting ONNX integration as architecture.
5. If successful, split Phase 1.5a/1.5b/Phase 2 into GitHub issues.

---

## Appendix A - Terms

- **VLM:** Vision-language model. A model that consumes images and text and emits text or structured output.
- **EP:** Execution Provider. ONNX Runtime's abstraction for CPU/GPU/NPU backends.
- **INID code:** Standard patent metadata identifier, such as `(54)` title or `(71)` applicant.
- **DocLayNet:** IBM's document-layout dataset with 11 categories.
- **PP-DocLayout-S:** A lightweight PaddlePaddle document layout detector with 23 categories, distributed under Apache-2.0.

## Appendix B - References

- PdfStruct repo: https://github.com/redoxnet/pdfstruct
- PP-DocLayout paper: https://arxiv.org/abs/2503.17213
- PP-DocLayout-S model card: https://huggingface.co/PaddlePaddle/PP-DocLayout-S
- ONNX Runtime C# documentation: https://onnxruntime.ai/docs/get-started/with-csharp.html
- ONNX Runtime execution providers: https://onnxruntime.ai/docs/execution-providers/
- DirectML execution provider: https://onnxruntime.ai/docs/execution-providers/DirectML-ExecutionProvider.html
- PaddleOCR ONNX conversion: https://paddlepaddle.github.io/PaddleOCR/main/en/version3.x/deployment/obtaining_onnx_models.html
- DocLayNet: https://github.com/DS4SD/DocLayNet
