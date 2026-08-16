using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Content;
using Microsoft.Xna.Framework.Graphics;
using xTile.Dimensions;
using xTile.Display;
using xTile.Layers;
using xTile.ObjectModel;
using xTile.Tiles;

namespace StardewModdingAPI.Framework.Rendering;

/// <summary>A map display device which overrides the draw logic to support tile rotation.</summary>
internal class SDisplayDevice : XnaDisplayDevice
{
    /*********
    ** Public methods
    *********/
    /// <summary>Construct an instance.</summary>
    /// <param name="contentManager">The content manager through which to load tiles.</param>
    /// <param name="graphicsDevice">The graphics device with which to render tiles.</param>
    public SDisplayDevice(ContentManager contentManager, GraphicsDevice graphicsDevice)
        : base(contentManager, graphicsDevice) { }

    /// <inheritdoc />
    protected override void DrawImpl(Tile tile, Location location, float layerDepth, Texture2D tileSheetTexture)
    {
        // Use xTile's simpler draw path for the overwhelming majority of tiles. Most tiles have no
        // custom properties at all, so avoid hashing both transform keys in that common case.
        IPropertyCollection properties = tile.Properties;
        if (properties.Count == 0)
        {
            base.DrawImpl(tile, location, layerDepth, tileSheetTexture);
            return;
        }

        // Look up each property once and reuse the values below if this tile is transformed.
        bool hasFlip = properties.TryGetValue("@Flip", out PropertyValue? flipValue);
        bool hasRotation = properties.TryGetValue("@Rotation", out PropertyValue? rotationValue);
        if (!hasFlip && !hasRotation)
        {
            base.DrawImpl(tile, location, layerDepth, tileSheetTexture);
            return;
        }

        // get rotation and effects
        float rotation = this.GetRotation(rotationValue);
        SpriteEffects effects = this.GetSpriteEffects(flipValue);
        var sourceRect = this.m_sourceRectangle;
        var origin = new Vector2(sourceRect.Width / 2f, sourceRect.Height / 2f);
        this.m_tilePosition.X += origin.X * Layer.zoom;
        this.m_tilePosition.Y += origin.Y * Layer.zoom;

        // apply
        this.m_spriteBatchAlpha.Draw(tileSheetTexture, this.m_tilePosition, sourceRect, this.m_modulationColour, rotation, origin, Layer.zoom, effects, layerDepth);
    }

    /// <summary>Get the sprite effects to apply for a tile.</summary>
    /// <param name="propertyValue">The raw flip property value, or <c>null</c> if none was set.</param>
    private SpriteEffects GetSpriteEffects(PropertyValue? propertyValue)
    {
        return propertyValue is not null && int.TryParse(propertyValue, out int value)
            ? (SpriteEffects)value
            : SpriteEffects.None;
    }

    /// <summary>Get the draw rotation to apply for a tile.</summary>
    /// <param name="propertyValue">The raw rotation property value, or <c>null</c> if none was set.</param>
    private float GetRotation(PropertyValue? propertyValue)
    {
        if (propertyValue is null || !int.TryParse(propertyValue, out int value))
            return 0;

        value %= 360;
        if (value == 0)
            return 0;

        return (float)(Math.PI / (180.0 / value));
    }
}
