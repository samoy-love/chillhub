use super::*;

#[test]
fn decoded_images_preserve_opaque_pixels_and_premultiply_transparency() {
    let mut pixels = [255, 80, 40, 255, 200, 100, 50, 128, 240, 10, 20, 0];
    premultiply_image(&mut pixels);
    assert_eq!(pixels, [255, 80, 40, 255, 100, 50, 25, 128, 0, 0, 0, 0]);
}

#[test]
fn decoded_images_stay_within_the_requested_dimensions_without_upscaling() {
    let temp = tempfile::tempdir().unwrap();
    let path = temp.path().join("wide.png");
    image::RgbaImage::from_pixel(160, 80, image::Rgba([200, 100, 50, 128]))
        .save(&path)
        .unwrap();
    let large = image_pixels(&path, 300, 300).unwrap();
    let small = image_pixels(&path, 40, 40).unwrap();
    assert_eq!((large.width(), large.height()), (160, 80));
    assert_eq!((small.width(), small.height()), (40, 20));
    assert_eq!(&small.as_bytes()[..4], &[100, 50, 25, 128]);
}

#[test]
fn stale_selection_cancels_before_decoding_and_corrupt_bundled_images_are_kept() {
    let temp = tempfile::tempdir().unwrap();
    let api = chillhub_native::net::ApiClient::new("http://127.0.0.1:1").unwrap();
    let missing = temp.path().join("not-requested.png");
    assert_eq!(
        image_pixels_for_epoch(&missing, 64, 64, Some(u64::MAX))
            .err()
            .unwrap(),
        "Изображение больше не выбрано"
    );
    let broken = temp.path().join("art/broken.png");
    std::fs::create_dir_all(broken.parent().unwrap()).unwrap();
    std::fs::write(&broken, b"broken image").unwrap();
    assert!(
        source_image_pixels(
            "art/broken.png",
            temp.path(),
            temp.path(),
            &api,
            64,
            64,
            None
        )
        .is_err()
    );
    assert_eq!(std::fs::read(&broken).unwrap(), b"broken image");
}
