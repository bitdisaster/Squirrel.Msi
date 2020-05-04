| README.md |
|:---|


# MSQ: Short for MSI Squirrel is a special version of Squirrel.Windows to work with MSI

![](docs/artwork/Squirrel-Logo.png)


MSQ is a set of tools to accomplish auto-updating with MSI used by Electron Apps. MSQ is specifically made for V3 of https://github.com/felixrieseberg/electron-wix-msi 


## What Do We Want?

For the longest time MSI was the gold standard in packaging and installing Windows applications.  That doesn't mean that they cant be auto-updated. The only functionality from the original Windows.Squirrel carried over is the updating process. Packaging and managing of shortcuts is of course done MSI.

* **Auto-Updating** MSQ provides auto-updating for Apps packaged  with [electron-wix-msi V3](https://github.com/felixrieseberg/electron-wix-msi)
* **Compatibility** App updates packaged with [Windows.Squirrel](https://github.com/Squirrel/Squirrel.Windows) can be updated by MSQ

## Building Squirrel
For the impatient:

```sh
git clone --recursive https://github.com/bitdisaster/squirrel.msi
cd squirrel.windows
.\.NuGet\NuGet.exe restore
msbuild /p:Configuration=Release
```
See [Contributing](docs/contributing/contributing.md) for additional information on building and contributing to Squirrel.


## License and Usage

See [COPYING](COPYING) for details on copyright and usage of the Squirrel.Windows software.









