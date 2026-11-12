# クローン先での起動

Unity Hubでこのフォルダを追加し、ProjectSettings/ProjectVersion.txtに記録されたUnity 6000.5.4f1で開きます。
初回のインポート・コンパイル後、保存済みのSampleSceneと共有Sceneビュー視点を自動で開きます。
未保存の変更や別の保存済みシーンを開いている場合は、自動切り替えを行いません。
手動の場合はAssets/Scenes/SampleScene.unityを開き、Tools > Scene View > Restore Shared Viewを実行してください。

Gameビューのカメラとレバーの配置・物理・操作設定はシーンとスクリプトで共有しています。
Play後はGameビューをクリックして入力にフォーカスしてください。Cキーでカメラを切り替えます。
元環境でSceneビューを変更した際はTools > Scene View > Save Shared Viewで保存してJSONもコミットしてください。
Library、Temp、UserSettingsは再生成されるローカル設定のため共有しません。
ウィンドウ配置・画面の縦横比やPC間の物理演算の完全一致は保証されません。
