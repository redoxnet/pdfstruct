// Copyright (c) Jong Hyun Kim. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using PdfStruct.Models;

namespace PdfStruct.Analysis;

/// <summary>
/// Attaches decoded machine-readable codes to a page's detected images. This is
/// the decoder-independent half of code handling: given the codes an
/// <see cref="ICodeDecoder"/> found, it tags the raster image a code sits
/// inside, or synthesises a code image when no raster image matches (the code
/// was drawn as vector graphics).
/// </summary>
public static class MachineReadableCodeDetector
{
    /// <summary>
    /// Returns the page's images with decoded codes attached. A code is matched
    /// to the first not-yet-matched image whose bounding box contains the code's
    /// centre; that image is tagged with the code's role, symbology, payload,
    /// and a <c>"machine-decoded"</c> provenance. A code that matches no image is
    /// appended as a new source-less image.
    /// </summary>
    /// <param name="images">The page's detected content images, in page order.</param>
    /// <param name="codes">The codes decoded on the page.</param>
    /// <returns>The augmented image list, or <paramref name="images"/> unchanged when there are no codes.</returns>
    public static IReadOnlyList<DetectedImage> Apply(
        IReadOnlyList<DetectedImage> images,
        IReadOnlyList<DecodedCode> codes)
    {
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(codes);
        if (codes.Count == 0) return images;

        var result = new List<DetectedImage>(images);
        var matched = new bool[result.Count];

        foreach (var code in codes)
        {
            var centerX = code.BoundingBox.CenterX;
            var centerY = code.BoundingBox.CenterY;

            var target = -1;
            for (var i = 0; i < result.Count; i++)
            {
                if (matched[i]) continue;
                if (Contains(result[i].BoundingBox, centerX, centerY))
                {
                    target = i;
                    break;
                }
            }

            if (target >= 0)
            {
                matched[target] = true;
                result[target] = result[target] with
                {
                    Role = code.Role,
                    CodeType = code.CodeType,
                    DecodedText = code.DecodedText,
                    AltSource = "machine-decoded",
                };
            }
            else
            {
                result.Add(new DetectedImage(
                    code.BoundingBox,
                    Source: null,
                    Role: code.Role,
                    CodeType: code.CodeType,
                    DecodedText: code.DecodedText,
                    AltSource: "machine-decoded"));
            }
        }

        return result;
    }

    /// <summary>Returns <c>true</c> when a point lies within a bounding box (inclusive).</summary>
    private static bool Contains(BoundingBox box, double x, double y) =>
        x >= box.Left && x <= box.Right && y >= box.Bottom && y <= box.Top;
}
