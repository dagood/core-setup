#!/usr/bin/env bash
#
# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.
# See the LICENSE file in the project root for more information.

# WARNING: This utility is not used by infra and very likely to be out of date.

# This is a simple dev utility to easily perform clean builds for Debian and RPM
# package development. It emulates the official build, first producing a
# portable build using some other image, then using Deb/RPM build images to
# package up the bits.
#
# Run this script from the root of the repository.

set -uex

skipPortable=
skipDeps=
onlyType=

while [[ $# > 0 ]]; do
    opt="$(echo "$1" | awk '{print tolower($0)}')"
    case "$opt" in
        --skip-portable)
            skipPortable=true
            ;;
        --skip-deps)
            skipDeps=true
            ;;
        --only-deb)
            onlyType=deb
            ;;
        --only-rpm)
            onlyType=rpm
            ;;
        *)
            echo "Invalid argument: $1"
            exit 1
            ;;
    esac
    shift
done

containerized() {
    image=$1
    shift
    docker run -it --rm \
        -u="$(id -u):$(id -g)" \
        -e HOME=/work/.container-home \
        -v "$(pwd):/work:z" \
        -w "/work" \
        "$image" \
        "$@"
}

package() {
    name=$1
    shift
    image=$1
    shift
    outputrid=$1
    shift
    ptype=$1
    shift
    queryCommand=$1
    shift

    if [ "$skipDeps" ] && [ "$outputrid" ]; then
        echo "Skipping $outputrid ($ptype)"
        return
    fi

    if [ "$onlyType" ] && [ "$onlyType" != "$ptype" ]; then
        echo "Skipping $outputrid ($ptype)"
        return
    fi

    containerized "$image" \
        Tools/msbuild.sh \
        build.proj \
        /t:BuildTraversalBuildDependencies \
        /p:ConfigurationGroup=Release \
        /p:OSGroup=Linux \
        /p:PortableBuild=false \
        /p:TargetArchitecture=x64 \
        "/bl:bin/msbuild.$name.traversaldependencies.binlog"

    containerized "$image" \
        Tools/msbuild.sh \
        src/pkg/packaging/dir.proj \
        /p:UsePrebuiltPortableBinariesForInstallers=true \
        /p:SharedFrameworkPublishDir=/work/bin/obj/linux-x64.Release/sharedFrameworkPublish/ \
        /p:InstallerSourceOSPlatformConfig=linux-x64.Release \
        /p:OutputRid=$outputrid \
        /p:GenerateProjectInstallers=true \
        /p:ConfigurationGroup=Release \
        /p:OSGroup=Linux \
        /p:PortableBuild=false \
        /p:TargetArchitecture=x64 \
        "/bl:bin/msbuild.$name.installers.binlog"

    containerized "$image" \
        find bin/*Release/ \
        -iname "*.$ptype" \
        -exec printf "\n{}\n========\n" \; \
        -exec $queryCommand '{}' \; \
        > "info-$ptype.txt"
}

[ "$skipPortable" ] || containerized microsoft/dotnet-buildtools-prereqs:centos-7-b46d863-20180719033416 \
    ./build.sh \
    -skiptests=true \
    -ConfigurationGroup=Release \
    -PortableBuild=true \
    -strip-symbols \
    -TargetArchitecture=x64 \
    -- \
    /bl:bin/msbuild.portable.binlog

ubuntu=microsoft/dotnet-buildtools-prereqs:ubuntu-14.04-debpkg-e5cf912-20175003025046
rhel=microsoft/dotnet-buildtools-prereqs:rhel-7-rpmpkg-c982313-20174116044113

package ubuntu $ubuntu '' deb "dpkg-deb -I"

for rid in \
  '' \
  sles.12-x64 \
  ;
do
  package rhel $rhel "$rid" rpm "rpm -qpiR"
done
