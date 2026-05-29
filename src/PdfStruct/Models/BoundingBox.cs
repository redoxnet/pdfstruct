// Copyright (c) Jong Hyun Kim. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace PdfStruct.Models;

/// <summary>
/// Represents a bounding box in PDF coordinate space.
/// Coordinates are in PDF points (72 points = 1 inch), origin at bottom-left.
/// Serialises to the four-element array <c>[left, bottom, right, top]</c>.
/// </summary>
/// <param name="Left">Left edge X coordinate.</param>
/// <param name="Bottom">Bottom edge Y coordinate.</param>
/// <param name="Right">Right edge X coordinate.</param>
/// <param name="Top">Top edge Y coordinate.</param>
public readonly record struct BoundingBox(
    double Left,
    double Bottom,
    double Right,
    double Top)
{
    /// <summary>Gets the width of the bounding box.</summary>
    public double Width => Right - Left;

    /// <summary>Gets the height of the bounding box.</summary>
    public double Height => Top - Bottom;

    /// <summary>Gets the horizontal center coordinate.</summary>
    public double CenterX => (Left + Right) / 2.0;

    /// <summary>Gets the vertical center coordinate.</summary>
    public double CenterY => (Bottom + Top) / 2.0;

    /// <summary>Gets the area of the bounding box.</summary>
    public double Area => Width * Height;

    /// <summary>
    /// Determines whether this bounding box overlaps with another.
    /// </summary>
    public bool Overlaps(BoundingBox other)
        => Left < other.Right && Right > other.Left
        && Bottom < other.Top && Top > other.Bottom;

    /// <summary>
    /// Computes the intersection area between this box and another.
    /// </summary>
    public double IntersectionArea(BoundingBox other)
    {
        var overlapLeft = Math.Max(Left, other.Left);
        var overlapRight = Math.Min(Right, other.Right);
        var overlapBottom = Math.Max(Bottom, other.Bottom);
        var overlapTop = Math.Min(Top, other.Top);

        if (overlapLeft >= overlapRight || overlapBottom >= overlapTop)
            return 0.0;

        return (overlapRight - overlapLeft) * (overlapTop - overlapBottom);
    }

    /// <summary>
    /// Creates a new bounding box that encompasses both this box and another.
    /// </summary>
    public BoundingBox Merge(BoundingBox other)
        => new(
            Math.Min(Left, other.Left),
            Math.Min(Bottom, other.Bottom),
            Math.Max(Right, other.Right),
            Math.Max(Top, other.Top));

    /// <summary>
    /// Converts to the array format <c>[left, bottom, right, top]</c>.
    /// </summary>
    public double[] ToArray() => [Left, Bottom, Right, Top];

    /// <summary>
    /// Creates a <see cref="BoundingBox"/> from a four-element <c>[left, bottom, right, top]</c> array.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when the array does not contain exactly 4 elements.</exception>
    public static BoundingBox FromArray(double[] coords)
    {
        if (coords.Length != 4)
            throw new ArgumentException("Bounding box requires exactly 4 coordinates.", nameof(coords));

        return new BoundingBox(coords[0], coords[1], coords[2], coords[3]);
    }
}
