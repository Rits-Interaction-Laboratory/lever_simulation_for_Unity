# Sceneビューの視点を共有する

1. 共有したいシーンを開き、Sceneビューを希望の視点に調整します。
2. Unity上部の Tools > Scene View > Save Shared View を実行します。
3. Assets/Editor/SharedSceneView.cs とその .meta、および ProjectSettings/SharedSceneViews/ 内の JSON をGitに追加します。
4. 別の環境でプロジェクトと同じシーンを開き、Tools > Scene View > Restore Shared View を実行します。

現在アクティブなシーンに対して1つの視点を保存します。再度保存すると上書きされます。
位置、角度、ズーム、平行投影、2Dモード、視野角を復元します。
Sceneビューの縦横比が異なる場合、画面端に映る範囲は変わります。
Main Cameraやシーン内のオブジェクトは変更しません。
復元は手動です。視点を動かしても、保存メニューを実行するまで共有設定は変わりません。
シーンの識別には .meta のGUIDを使用するため、シーンの .meta も通常どおりGitで共有してください。
