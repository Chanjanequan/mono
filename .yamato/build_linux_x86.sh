sudo apt-get install -y schroot
sudo apt-get install -y binutils debootstrap
git submodule update --init --recursive
git submodule update --init --recursive
cd external/buildscripts
./bee
cd ../..
perl external/buildscripts/build_runtime_linux.pl --stevedorebuilddeps=1
echo "Making directory incomingbuilds/linux32"
mkdir -p incomingbuilds/linux32
ls -al incomingbuilds/linux32
echo "Copying builds to incomingbuilds"
cp -r -v builds/* incomingbuilds/linux32/
ls -al incomingbuilds/linux32