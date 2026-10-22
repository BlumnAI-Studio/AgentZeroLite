# Ubuntu 24.04 desktop runtime for trying the linux-x64 build without a Linux machine.
# The app is not baked in: mount a publish folder (or install the .deb) at run time.
#
#   docker build -t azl-ubuntu-gui Project/AgentZeroAvalonia/linux/test
#   ./Project/AgentZeroAvalonia/linux/test/run-gui.sh publish-avalonia/linux-x64     (WSLg / X11)
#
# On Windows 11 the GUI shows through WSLg: Docker Desktop exposes WSLg's X11 socket at
# /run/desktop/mnt/host/wslg, so no X server has to be installed.
FROM ubuntu:24.04

ENV DEBIAN_FRONTEND=noninteractive LANG=C.UTF-8 LC_ALL=C.UTF-8

# What the .deb declares as Depends (keep linux/deb/control in step), plus test tools:
# x11-utils/xdotool to find and poke the window, imagemagick to capture it, a CJK font
# for the Korean UI, bash only (no zsh) — a stock Ubuntu.
RUN apt-get update && apt-get install -y --no-install-recommends \
        libicu74 libssl3t64 libfontconfig1 libfreetype6 ca-certificates \
        libx11-6 libice6 libsm6 libxext6 libxrandr2 libxi6 libxcursor1 \
        libgtk-3-0t64 libwebkit2gtk-4.1-0 libsoup-3.0-0 \
        fonts-noto-core fonts-noto-cjk fonts-noto-color-emoji \
        xdg-utils git curl \
        x11-utils xdotool imagemagick sqlite3 \
    && rm -rf /var/lib/apt/lists/*

# A desktop-like user (the app keeps its data under ~/.local/share).
RUN useradd -m -s /bin/bash az
USER az
WORKDIR /home/az
