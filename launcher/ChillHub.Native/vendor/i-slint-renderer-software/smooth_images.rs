// Copyright © 2026 Chill Hub contributors
// SPDX-License-Identifier: GPL-3.0-only OR LicenseRef-Slint-Royalty-free-2.0 OR LicenseRef-Slint-Software-3.0

//! Bilinear colour-image sampling. Glyphs retain the upstream rasterizer.
use super::draw_functions::{PremultipliedRgbaColor, TargetPixel};
use super::{Fixed, PhysicalLength, PhysicalRect, SceneTexture};
use i_slint_core::graphics::TexturePixelFormat;
use i_slint_core::lengths::PointLengths;

pub(super) fn draw(
    span: &PhysicalRect,
    line: PhysicalLength,
    texture: &SceneTexture,
    pixels: &mut [impl TargetPixel],
    clip_begin: i16,
    clip_end: i16,
) -> bool {
    if !matches!(
        texture.format,
        TexturePixelFormat::Rgb | TexturePixelFormat::Rgba | TexturePixelFormat::RgbaPremultiplied
    ) {
        return false;
    }
    let e = &texture.extra;
    if e.dx.0 == 256 && e.dy.0 == 256 && e.off_x.0 & 15 == 0 && e.off_y.0 & 15 == 0 {
        return false;
    }
    let size = texture.source_size();
    if size.width <= 0 || size.height <= 0 {
        return true;
    }
    let y = (line - span.origin.y_length()).get() as i32;
    let y = if e.rotation.mirror_width() {
        span.size.height as i32 - y - 1
    } else {
        y
    };
    let off_x = Fixed::<i32, 8>::from_fixed(e.off_x).0;
    let off_y = Fixed::<i32, 8>::from_fixed(e.off_y).0;
    let dx = e.dx.0 as i32;
    let dy = e.dy.0 as i32;
    let len = pixels.len() as i32;
    // Most launcher images are premultiplied, upright and do not repeat.
    // Hoist vertical sampling and avoid two integer divisions per output pixel.
    let first_x = off_x + dx * clip_begin as i32;
    let last_x = first_x + dx * (len - 1);
    if texture.format == TexturePixelFormat::RgbaPremultiplied
        && !e.rotation.is_transpose()
        && !e.rotation.mirror_width()
        && !e.rotation.mirror_height()
        && e.colorize.alpha() == 0
        && first_x >= 0
        && last_x < size.width as i32 * 256
        && dx >= 0
    {
        let sy = (off_y + dy * y).rem_euclid(size.height as i32 * 256) + dy / 2 - 128;
        draw_premultiplied_row(
            texture,
            pixels,
            first_x + dx / 2 - 128,
            dx,
            sy,
            size.width as usize,
            size.height as usize,
        );
        return true;
    }
    for (i, pixel) in pixels.iter_mut().enumerate() {
        let x = if e.rotation.mirror_height() {
            clip_end as i32 + len - 1 - i as i32
        } else {
            clip_begin as i32 + i as i32
        };
        let (sx, sy) = if e.rotation.is_transpose() {
            (y, x)
        } else {
            (x, y)
        };
        // Match texture pixel centres, clamp taps at cropped-image edges, and
        // retain repeated-image coordinates without reading adjacent atlas rows.
        let sx = (off_x + dx * sx).rem_euclid(size.width as i32 * 256) + dx / 2 - 128;
        let sy = (off_y + dy * sy).rem_euclid(size.height as i32 * 256) + dy / 2 - 128;
        let mut c = sample(texture, sx, sy, size.width as usize, size.height as usize);
        if e.colorize.alpha() != 0 {
            c.red = (e.colorize.red() as u16 * c.alpha as u16 / 255) as u8;
            c.green = (e.colorize.green() as u16 * c.alpha as u16 / 255) as u8;
            c.blue = (e.colorize.blue() as u16 * c.alpha as u16 / 255) as u8;
        }
        if e.alpha != 255 {
            c.red = (c.red as u16 * e.alpha as u16 / 255) as u8;
            c.green = (c.green as u16 * e.alpha as u16 / 255) as u8;
            c.blue = (c.blue as u16 * e.alpha as u16 / 255) as u8;
            c.alpha = (c.alpha as u16 * e.alpha as u16 / 255) as u8;
        }
        pixel.blend(c);
    }
    true
}

#[inline]
fn packed_pixel(data: &[u8], x: usize) -> u64 {
    let p = &data[x * 4..x * 4 + 4];
    let value = u32::from_le_bytes(p.try_into().unwrap()) as u64;
    let pairs = (value | value << 16) & 0x0000_ffff_0000_ffff;
    (pairs | pairs << 8) & 0x00ff_00ff_00ff_00ff
}

#[inline]
fn interpolate(a: u64, b: u64, fraction: u64) -> u64 {
    // Four independent 16-bit lanes. Each sum is <= 65408, so no carry
    // crosses a channel boundary. Two rounded passes differ by at most 1/255.
    ((a * (256 - fraction) + b * fraction + 0x0080_0080_0080_0080) >> 8) & 0x00ff_00ff_00ff_00ff
}

fn draw_premultiplied_row(
    texture: &SceneTexture,
    pixels: &mut [impl TargetPixel],
    mut x: i32,
    dx: i32,
    y: i32,
    width: usize,
    height: usize,
) {
    let y = y.clamp(0, (height as i32 - 1) * 256) as usize;
    let (y0, y1) = (y >> 8, ((y >> 8) + 1).min(height - 1));
    let stride = texture.pixel_stride as usize * 4;
    let row0 = &texture.data[y0 * stride..y0 * stride + width * 4];
    let row1 = &texture.data[y1 * stride..y1 * stride + width * 4];
    let fy = (y & 255) as u64;
    let max_x = (width as i32 - 1) * 256;
    for pixel in pixels {
        let sx = x.clamp(0, max_x) as usize;
        let x0 = sx >> 8;
        let x1 = (x0 + 1).min(width - 1);
        let fx = (sx & 255) as u64;
        let top = interpolate(packed_pixel(row0, x0), packed_pixel(row0, x1), fx);
        let bottom = interpolate(packed_pixel(row1, x0), packed_pixel(row1, x1), fx);
        let p = interpolate(top, bottom, fy);
        let mut color = PremultipliedRgbaColor {
            red: p as u8,
            green: (p >> 16) as u8,
            blue: (p >> 32) as u8,
            alpha: (p >> 48) as u8,
        };
        if texture.extra.alpha != 255 {
            let alpha = texture.extra.alpha as u16;
            color.red = (color.red as u16 * alpha / 255) as u8;
            color.green = (color.green as u16 * alpha / 255) as u8;
            color.blue = (color.blue as u16 * alpha / 255) as u8;
            color.alpha = (color.alpha as u16 * alpha / 255) as u8;
        }
        if color.alpha == 255 {
            *pixel = TargetPixel::from_rgb(color.red, color.green, color.blue);
        } else {
            pixel.blend(color);
        }
        x += dx;
    }
}

fn sample(
    texture: &SceneTexture,
    x: i32,
    y: i32,
    width: usize,
    height: usize,
) -> PremultipliedRgbaColor {
    let x = x.clamp(0, (width as i32 - 1) * 256) as usize;
    let y = y.clamp(0, (height as i32 - 1) * 256) as usize;
    let (x0, y0) = (x >> 8, y >> 8);
    let (x1, y1) = ((x0 + 1).min(width - 1), (y0 + 1).min(height - 1));
    let read = |x: usize, y: usize| {
        let bpp = texture.format.bpp();
        let p = (y * texture.pixel_stride as usize + x) * bpp;
        let d = &texture.data[p..p + bpp];
        let alpha = if bpp == 3 { 255 } else { d[3] };
        let component = |v: u8| {
            if texture.format == TexturePixelFormat::Rgba {
                (v as u16 * alpha as u16 / 255) as u8
            } else {
                v
            }
        };
        PremultipliedRgbaColor {
            red: component(d[0]),
            green: component(d[1]),
            blue: component(d[2]),
            alpha,
        }
    };
    let taps = [read(x0, y0), read(x1, y0), read(x0, y1), read(x1, y1)];
    let (fx, fy) = ((x & 255) as u32, (y & 255) as u32);
    let weights = [
        (256 - fx) * (256 - fy),
        fx * (256 - fy),
        (256 - fx) * fy,
        fx * fy,
    ];
    let channel = |get: fn(PremultipliedRgbaColor) -> u8| -> u8 {
        ((taps
            .iter()
            .zip(weights)
            .map(|(&p, w)| get(p) as u32 * w)
            .sum::<u32>()
            + 32768)
            >> 16) as u8
    };
    PremultipliedRgbaColor {
        red: channel(|p| p.red),
        green: channel(|p| p.green),
        blue: channel(|p| p.blue),
        alpha: channel(|p| p.alpha),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::{RenderingRotation, SceneTextureExtra};
    use alloc::vec::Vec;
    fn texture(data: &[u8], format: TexturePixelFormat, stride: u16) -> SceneTexture<'_> {
        SceneTexture {
            data,
            format,
            pixel_stride: stride,
            extra: SceneTextureExtra {
                dx: Fixed(128),
                dy: Fixed(128),
                off_x: Fixed(0),
                off_y: Fixed(0),
                colorize: Default::default(),
                alpha: 255,
                rotation: RenderingRotation::NoRotation,
            },
        }
    }
    #[test]
    fn blends_four_taps_and_clamps_each_edge() {
        let t = texture(
            &[0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0, 255],
            TexturePixelFormat::Rgb,
            2,
        );
        let p = sample(&t, 128, 128, 2, 2);
        assert_eq!((p.red, p.green, p.blue, p.alpha), (64, 64, 64, 255));
        let p = sample(&t, 999, -999, 2, 2);
        assert_eq!((p.red, p.green, p.blue), (255, 0, 0));
    }
    #[test]
    fn transparent_rgb_does_not_bleed_into_visible_edges() {
        let t = texture(&[255, 0, 0, 0, 0, 0, 255, 255], TexturePixelFormat::Rgba, 2);
        let p = sample(&t, 128, 0, 2, 1);
        assert_eq!((p.red, p.green, p.blue, p.alpha), (0, 0, 128, 128));
    }
    #[test]
    fn premultiplied_pixels_are_not_multiplied_twice() {
        let t = texture(&[0, 64, 0, 128], TexturePixelFormat::RgbaPremultiplied, 1);
        let p = sample(&t, -128, 999, 1, 1);
        assert_eq!((p.green, p.alpha), (64, 128));
    }
    #[test]
    fn cropped_stride_does_not_sample_neighbouring_pixels() {
        let t = texture(
            &[255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 0, 0],
            TexturePixelFormat::Rgb,
            3,
        );
        let p = sample(&t, 128, 128, 1, 2);
        assert_eq!((p.red, p.green, p.blue), (255, 0, 0));
    }
    #[test]
    fn enlarged_image_uses_pixel_centres_and_clipped_spans_match() {
        let mut t = texture(&[0, 0, 0, 255, 255, 255], TexturePixelFormat::Rgb, 2);
        t.extra.dy = Fixed(256);
        let span = euclid::rect(0, 0, 4, 1);
        let mut full = [PremultipliedRgbaColor::default(); 4];
        assert!(draw(&span, PhysicalLength::new(0), &t, &mut full, 0, 0));
        assert_eq!(full.map(|p| p.red), [0, 64, 191, 255]);
        let mut clipped = [PremultipliedRgbaColor::default(); 2];
        assert!(draw(&span, PhysicalLength::new(0), &t, &mut clipped, 1, 1));
        assert_eq!(clipped.map(|p| p.red), [64, 191]);
    }
    #[test]
    fn opacity_and_colorization_preserve_premultiplied_bounds() {
        let mut t = texture(&[255, 255, 255, 128], TexturePixelFormat::Rgba, 1);
        t.extra.colorize = i_slint_core::Color::from_rgb_u8(0, 255, 0);
        t.extra.alpha = 128;
        let mut pixels = [PremultipliedRgbaColor::default(); 2];
        assert!(draw(
            &euclid::rect(0, 0, 2, 1),
            PhysicalLength::new(0),
            &t,
            &mut pixels,
            0,
            0
        ));
        for p in pixels {
            assert_eq!((p.red, p.green, p.blue, p.alpha), (0, 64, 0, 64));
        }
    }
    #[test]
    fn exact_size_and_glyph_textures_retain_upstream_path() {
        let mut t = texture(&[1, 2, 3], TexturePixelFormat::Rgb, 1);
        t.extra.dx = Fixed(256);
        t.extra.dy = Fixed(256);
        let mut pixels = [PremultipliedRgbaColor::default(); 1];
        assert!(!draw(
            &euclid::rect(0, 0, 1, 1),
            PhysicalLength::new(0),
            &t,
            &mut pixels,
            0,
            0
        ));
        t.format = TexturePixelFormat::AlphaMap;
        t.extra.dx = Fixed(128);
        assert!(!draw(
            &euclid::rect(0, 0, 1, 1),
            PhysicalLength::new(0),
            &t,
            &mut pixels,
            0,
            0
        ));
    }
    #[test]
    fn fast_premultiplied_rows_match_reference_with_one_level_rounding() {
        let bytes: Vec<u8> = (0..64)
            .flat_map(|i| {
                let alpha = ((i * 47) % 256) as u8;
                [alpha / 2, alpha / 3, alpha, alpha]
            })
            .collect();
        let t = texture(&bytes, TexturePixelFormat::RgbaPremultiplied, 8);
        for y in [-128, 0, 77, 127, 255, 511, 1792, 9999] {
            for dx in [1, 64, 128, 256, 357] {
                let mut out = [PremultipliedRgbaColor::default(); 13];
                draw_premultiplied_row(&t, &mut out, -128, dx, y, 8, 8);
                for (i, p) in out.iter().enumerate() {
                    let reference = sample(&t, -128 + i as i32 * dx, y, 8, 8);
                    for (got, expected) in [
                        (p.red, reference.red),
                        (p.green, reference.green),
                        (p.blue, reference.blue),
                        (p.alpha, reference.alpha),
                    ] {
                        assert!(got.abs_diff(expected) <= 1, "y={y}, dx={dx}, x={i}");
                    }
                    assert!(p.red <= p.alpha && p.green <= p.alpha && p.blue <= p.alpha);
                }
            }
        }
    }
}
