# Импорт локального игрового видео

Инструкция для добавления разрешённой локальной записи в карточку игры. Скрипт
импорта не скачивает исходник и не публикует его. Для него нужны Node.js 24,
`ffmpeg` и `ffprobe`.

## Подготовить ролик

Подготовьте исходный видеофайл и локальный JPEG-постер. Исходник должен быть не
больше 10 GiB, длительностью до четырёх часов и иметь разрешение не ниже 480p.
HDR и видео с поворотом сначала нужно привести к обычному SDR-видео правильной
ориентации. Скрипт создаёт H.264/AAC MP4-версии до 1080p и не увеличивает
разрешение исходника.

Если рабочего плана медиа ещё нет, создайте его штатным процессом подготовки
Steam-медиа. Не запускайте эту команду после импорта: она заново создаёт план и
стирает добавленную запись.

```sh
node scripts/content/prepare-media.mjs
```

Импортируйте локальные файлы. Укажите существующий `gameId`, имя автора и
русскую подпись:

```sh
node scripts/content/prepare-gameplay.mjs \
  --game-id lethal-company \
  --input /path/to/authorized-recording.mp4 \
  --poster /path/to/poster.jpg \
  --author "Имя автора" \
  --caption "Коротко о том, что происходит в ролике"
```

Команда обновит локальный план в `scripts/content/.cache/media-upload-plan.json`
и сохранит подготовленные файлы в `scripts/content/.cache/release-media/`. Эти
файлы пока никуда не отправляются.

## Собрать карточки и сверить контрольные суммы

Обновите release-манифест из плана, удалив локальные пути. Эта команда не
перекодирует медиа:

```sh
node --input-type=module -e 'import {readFileSync,writeFileSync} from "node:fs"; const plan=JSON.parse(readFileSync("scripts/content/.cache/media-upload-plan.json","utf8")); writeFileSync("scripts/content/media-release.json",JSON.stringify({files:plan.files.map(({localPath,...file})=>file)},null,2)+"\n");'
```

Создайте данные карточек, упакуйте офлайн-изображения и проверьте их вместе с
подготовленными медиа:

```sh
node scripts/content/build-presentation.mjs
uv run --with pillow==11.3.0 --with blake3==1.0.8 python scripts/content/prepare-bundled-media.py
node scripts/content/validate-presentation.mjs --prepared
```

Просмотрите изменения в `launcher/ChillHub.Native/Data/`, `launcher/ChillHub.Native/Assets/`
и `launcher/ChillHub.Native/art/`, а также обновлённый
`scripts/content/media-release.json`. Добавляйте в PR только проверенные
результаты.

## Выкатить медиа и опубликовать карточки

После слияния изменений выкатите медиа штатной целью deploy-kit:

```sh
dk deploy chillhub-media
```

Сборка этой цели обновляет историю опубликованных медиа и формирует проверенный
`stage-media` из release-манифеста и ранее опубликованных файлов. Для проверки и
применения карточек требуется `CHILLHUB_ADMIN_COOKIE` в текущем окружении.
Задайте его в своей сессии и сначала запустите dry-run:

```sh
node scripts/content/publish-presentation.mjs
```

Dry-run обращается к админскому и публичному API и сохраняет резервную копию и
предложение локально. После просмотра результата и успешной выкладки медиа
примените обновление:

```sh
node scripts/content/publish-presentation.mjs --apply
```

Скрипт проверяет, что медиа уже доступно по опубликованным адресам, сохраняет
предложение и использует revision check при записи. Не запускайте `--apply`, пока
выкатка медиа не завершилась успешно.
